using System.Text.RegularExpressions;

namespace SpecHygiene.Analysis.Reqnroll;

/// <summary>
/// Decides whether a step-definition pattern is a raw regex or a Cucumber Expression — the routing
/// that picks which of <see cref="MatchingLogic"/>'s two match functions applies.
/// <para>
/// This is a VERBATIM port of Reqnroll.Bindings.CucumberExpressions.CucumberExpressionDetector
/// (identical across Reqnroll v3.0.0–v3.3.3 and main — the sample suite runs 3.3.3). Matching the oracle exactly
/// is mandatory, not an optimisation. Send a raw regex like <c>I have (\d+) items</c> through the
/// Cucumber compiler and its <c>(\d+)</c> is read as OPTIONAL TEXT — a regex that can never match the
/// real step, i.e. a false "dead" verdict for a live binding (and, before hardening, an infinite loop
/// in the Cucumber compiler on the stray <c>\d</c>). The two grammars overlap syntactically and only
/// this check tells them apart.
/// </para>
/// <para>
/// The order is load-bearing: the <c>{param}</c> placeholder check runs BEFORE the regex-construct
/// checks, so a pattern carrying a Cucumber placeholder stays a Cucumber Expression even if it also
/// contains something that looks like <c>\d+</c> or <c>(x+)</c>.
/// </para>
/// </summary>
public static class CucumberExpressionDetector
{
    // A Cucumber Expression parameter placeholder, e.g. {}, {int}, {word}.
    private static readonly Regex ParameterPlaceholder = new(@"{\w*}");

    // Regex constructs that don't occur in Cucumber Expressions: a parenthesised group ending in a
    // quantifier — (…+) / (…*) — or a bare .*
    private static readonly Regex CommonRegexStepDefPatterns = new(@"(\([^\)]+[\*\+]\)|\.\*)");

    // Escaped regex metacharacters that are invalid in Cucumber Expressions: \. and \d+
    private static readonly Regex ExtendedRegexStepDefPatterns = new(@"(\\\.|\\d\+)");

    /// <summary>
    /// True when Reqnroll would parse <paramref name="pattern"/> as a Cucumber Expression rather than
    /// a regex. Mirrors CucumberExpressionDetector.IsCucumberExpression exactly.
    /// </summary>
    public static bool IsCucumberExpression(string pattern)
    {
        if (pattern is null) throw new ArgumentNullException(nameof(pattern));

        if (pattern.StartsWith("^", StringComparison.Ordinal) || pattern.EndsWith("$", StringComparison.Ordinal))
            return false;

        if (ParameterPlaceholder.IsMatch(pattern))
            return true;

        if (CommonRegexStepDefPatterns.IsMatch(pattern))
            return false;

        if (ExtendedRegexStepDefPatterns.IsMatch(pattern))
            return false;

        return true;
    }
}
