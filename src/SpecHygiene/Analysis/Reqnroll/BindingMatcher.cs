using System.Text.RegularExpressions;
using SpecHygiene.Models;

namespace SpecHygiene.Analysis.Reqnroll;

/// <summary>
/// One step definition prepared for matching: its pattern routed to the right grammar and compiled
/// once, or marked indeterminate with a reason.
/// </summary>
public sealed class CompiledBinding
{
    public required StepDefinitionInfo Definition { get; init; }

    /// <summary>Compiled Cucumber Expression (anchored). Null for raw-regex or indeterminate.</summary>
    public Regex? Cucumber { get; init; }

    /// <summary>Raw-regex pattern, compiled ONCE (anchored per Reqnroll's textual rule). Null for
    /// Cucumber or indeterminate. Stored compiled, not as a string, so the hot loop reuses it rather
    /// than recompiling per step.</summary>
    public Regex? RawRegex { get; init; }

    /// <summary>
    /// Why this binding could not be evaluated. Non-null means it must NEVER be reported dead: we
    /// could not determine whether any step matches it, and "we don't know" is not "unused".
    /// </summary>
    public string? IndeterminateReason { get; init; }

    public bool IsIndeterminate => IndeterminateReason is not null;

    /// <summary>
    /// True when the pattern is plain literal text — no placeholders, no optional text, no
    /// alternation, no regex. Such a binding matches exactly one step text, so it can be resolved by
    /// dictionary lookup. Anything else MUST be scanned against every step: a parametric binding like
    /// "I wait {int} seconds" is bound by "I wait 5 seconds", which no lookup keyed on pattern text
    /// would ever find.
    /// </summary>
    public bool IsLiteral { get; init; }
}

/// <summary>
/// Prepares step definitions for matching and answers "does this step text match this binding?" the
/// way Reqnroll really would.
/// <para>
/// Replaces the previous five-strategy regex ladder, whose Strategy 3 accepted an UNANCHORED partial
/// match — a confirmed false-"alive" source, since Reqnroll always requires a full match. Correcting
/// it means some bindings previously reported used are now correctly reported dead.
/// </para>
/// <para>
/// The safety rule throughout: a binding is reported dead ONLY if its pattern was successfully
/// evaluated. Anything we cannot compile becomes indeterminate, never dead.
/// </para>
/// </summary>
public sealed class BindingMatcher
{
    private readonly IReadOnlyDictionary<string, string> _parameterTypeFragments;

    /// <summary>A regex that matches nothing — for a convention binding that can never bind (R9,
    /// parameterised bare [Given]). Compiled once; its verdict is "no step matches", i.e. unused.</summary>
    private static readonly Regex NeverMatches = new(@"(?!)", RegexOptions.Compiled);

    /// <param name="parameterTypeFragments">
    /// Regex fragment per Cucumber parameter type name. Defaults to Reqnroll's 20 built-ins; custom
    /// enum / [StepArgumentTransformation] types are added by the Roslyn pass. A pattern referencing
    /// a type absent from this map compiles to nothing and becomes indeterminate — which is why the
    /// map being incomplete costs coverage but never correctness.
    /// </param>
    public BindingMatcher(IReadOnlyDictionary<string, string>? parameterTypeFragments = null)
        => _parameterTypeFragments = parameterTypeFragments ?? DefaultCucumberExpressionParameterTypes.Fragments;

    /// <summary>Route and compile one definition. Never throws — failures become indeterminate.</summary>
    public CompiledBinding Compile(StepDefinitionInfo definition)
    {
        // Pattern is the verbatim [Given("…")] text. RegexPattern is the old parser's massaged form —
        // using it here would double-process the pattern before Reqnroll's own grammar sees it.
        var pattern = definition.Pattern;

        // The attribute HAS a pattern argument but it is not a readable literal (interpolated string,
        // const, concat). Its real text is unknown, so we cannot say what it matches — indeterminate,
        // never dead. Crucially this must be checked BEFORE the empty-pattern convention path, or an
        // unreadable pattern would be mis-bound to the method name and reported as a false "unused".
        if (definition.UnresolvablePattern)
            return new CompiledBinding { Definition = definition, IndeterminateReason = "pattern is not a readable string literal (interpolated/const) — cannot evaluate" };

        if (string.IsNullOrEmpty(pattern))
        {
            // R9: a bare [Given] with no pattern string binds by the PascalCase method name — but only
            // when the method takes NO parameters. A parameterised method under bare convention never
            // binds at all, so it can match no step: evaluable and (correctly) unused, NOT indeterminate
            // — leaving it indeterminate would hide a binding that genuinely cannot be reached.
            var conv = MethodNameConvention.GenerateConventionPattern(definition.MethodName, definition.ParameterCount);
            if (conv is null)
                return new CompiledBinding { Definition = definition, RawRegex = NeverMatches, IsLiteral = false };

            // Convention match is case-insensitive and tolerates a trailing period, so it is NOT a
            // case-sensitive literal — must be scanned, not put in the literal fast-path dictionary.
            return new CompiledBinding
            {
                Definition = definition,
                RawRegex = MethodNameConvention.CompileConventionRegex(conv),
                IsLiteral = false,
            };
        }

        if (!CucumberExpressionDetector.IsCucumberExpression(pattern))
        {
            // Compile ONCE here. This both validates (a malformed regex surfaces as indeterminate,
            // not a silent never-match that reads as "dead") and gives the hot loop a Regex to reuse
            // instead of recompiling the pattern for every step it is tested against.
            try
            {
                var rx = new Regex(MatchingLogic.EffectiveRegexPattern(pattern));
                return new CompiledBinding { Definition = definition, RawRegex = rx };
            }
            catch (ArgumentException ex)
            {
                return new CompiledBinding { Definition = definition, IndeterminateReason = $"invalid regex pattern: {ex.Message}" };
            }
        }

        try
        {
            return new CompiledBinding
            {
                Definition = definition,
                Cucumber = PatternCompiler.CompilePattern(pattern, _parameterTypeFragments),
                IsLiteral = IsLiteralCucumberText(pattern),
            };
        }
        catch (InvalidOperationException ex)
        {
            // Unregistered {CustomType} — an enum or [StepArgumentTransformation] we have not resolved.
            return new CompiledBinding { Definition = definition, IndeterminateReason = ex.Message };
        }
        catch (ArgumentException ex)
        {
            // The compiled fragment was not a valid regex.
            return new CompiledBinding { Definition = definition, IndeterminateReason = $"pattern did not compile: {ex.Message}" };
        }
    }

    /// <summary>
    /// True when a Cucumber Expression is plain literal text: no placeholder, optional-text group,
    /// '/' alternation, or escape. Conservative — anything it is unsure about is treated as
    /// non-literal, which only costs a scan, never correctness.
    /// </summary>
    private static bool IsLiteralCucumberText(string pattern) =>
        pattern.IndexOfAny(new[] { '{', '}', '(', ')', '/', '\\' }) < 0;

    /// <summary>
    /// True when Reqnroll would bind <paramref name="stepText"/> to this binding. Indeterminate
    /// bindings never match — they are accounted for separately, not silently treated as dead.
    /// </summary>
    public static bool Matches(CompiledBinding binding, string stepText)
    {
        if (binding.IsIndeterminate) return false;

        if (binding.Cucumber is not null)
            return MatchingLogic.IsFullMatch(binding.Cucumber, stepText);

        if (binding.RawRegex is not null)
        {
            try { return binding.RawRegex.IsMatch(stepText); }   // precompiled at Compile time
            catch (ArgumentException) { return false; }   // malformed regex: reported via indeterminate at compile
        }

        return false;
    }
}
