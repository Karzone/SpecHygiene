// Pure, unit-testable Cucumber-Expression-placeholder compilation, extracted from the
// idea (not yet implemented in Program.cs) of correctly resolving custom parameter
// types - enum-derived and [StepArgumentTransformation]-derived - into the compiled
// Regex the way Reqnroll really does.
//
// Program.cs currently drives the REAL Reqnroll CucumberExpressionParameterTypeRegistry
// via reflection, but backs it with a `StubBindingRegistry` that never registers any
// custom parameter types (see StubBindingRegistry in Program.cs - every
// Register*Binding method is a no-op). That means today, any pattern using a custom
// enum or StepArgumentTransformation placeholder - e.g. "{PolicyStatus}" - fails to
// resolve inside the real CucumberExpression parser and the binding is silently
// excluded from the dead/alive verdict entirely (counted as a parse failure), rather
// than being matched correctly. This file is a small, isolated, testable stand-in for
// "given a set of known custom-parameter-type regex fragments, compile a pattern into
// the Regex Reqnroll would actually use" - proven correct here in isolation before any
// attempt to wire real enum/transform discovery back into Program.cs (out of scope for
// this task).
//
// Confirmed ground truth this class must satisfy (see
// scratchpad/reqnroll-quirks-verification/ for the empirical verification):
//   - A `{EnumTypeName}` placeholder auto-registers and matches any of that enum's
//     member names, CASE-INSENSITIVELY, regardless of namespace.
//   - [StepArgumentTransformation(regex, Name = "x")] registers a `{x}` placeholder
//     using the given regex verbatim (no forced case-insensitivity).
//   - [StepArgumentTransformation(regex)] with no Name registers a placeholder named
//     after the transform method's return type's simple name.
//   - Reqnroll always requires a FULL match of the entire step text (see
//     MatchingLogic.IsFullMatch) - so the compiled pattern here is anchored ^...$.
//   - Confirmed production regression (2026-07-07, see PatternCompilerTests'
//     "ProductionRegression" cases): unescaped "(text)" outside a placeholder is
//     OPTIONAL TEXT (matches with or without "text"); "\(", "\)", "\/" are escapes for
//     LITERAL '(' ')' '/'. Previously this method Regex.Escape()'d all of these as
//     plain literal characters, which produced a compiled regex that could never match
//     the real step text for any such pattern - a confirmed false "dead" verdict for
//     live bindings in Acme.Api.DataSet and Acme.Api.Estimate.
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SpecHygiene.Analysis.Reqnroll
{
    public static class PatternCompiler
    {
        /// <summary>
        /// Compiles a Cucumber-Expression-style pattern (literal text, `{TypeName}`
        /// placeholders, "(optional text)", and "\(" / "\)" / "\/" literal escapes) into
        /// a fully-anchored Regex, substituting each placeholder with the regex fragment
        /// supplied for its type name in <paramref name="customParameterTypeRegexFragments"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pattern references a `{TypeName}` placeholder with no entry in
        /// <paramref name="customParameterTypeRegexFragments"/> - mirrors Reqnroll refusing
        /// to compile a Cucumber Expression that references an unregistered parameter type.
        /// </exception>
        public static Regex CompilePattern(string pattern, IReadOnlyDictionary<string, string> customParameterTypeRegexFragments)
        {
            if (pattern is null) throw new ArgumentNullException(nameof(pattern));
            if (customParameterTypeRegexFragments is null) throw new ArgumentNullException(nameof(customParameterTypeRegexFragments));

            // NOT RegexOptions.Compiled: with thousands of bindings, IL-generating each one costs
            // minutes up front (single-threaded, before the match loop even starts). The token index
            // means each step now tests only a handful of bindings, so interpreted matching is cheap
            // and the compile cost is pure loss. Does not affect match results either way.
            return new Regex("^" + CompileSegment(pattern, customParameterTypeRegexFragments) + "$");
        }

        /// <summary>
        /// Compiles one segment of pattern text (the whole pattern on the outermost call,
        /// or the inner text of an optional-text group on a recursive call) into an
        /// unanchored regex fragment.
        /// </summary>
        private static string CompileSegment(string segment, IReadOnlyDictionary<string, string> customParameterTypeRegexFragments)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < segment.Length)
            {
                char c = segment[i];

                if (c == '\\')
                {
                    // Cucumber: a backslash escapes the following char, which then stands for itself
                    // literally. Reqnroll only defines escapes for (){}/\ , but we consume ANY
                    // backslash-pair so a stray escape (e.g. a regex that slipped past the detector)
                    // can never fall into the word branch below and spin forever. A trailing backslash
                    // (no next char) is treated as a literal backslash. Advancing i is what matters.
                    if (i + 1 < segment.Length)
                    {
                        sb.Append(Regex.Escape(segment[i + 1].ToString()));
                        i += 2;
                    }
                    else
                    {
                        sb.Append(Regex.Escape("\\"));
                        i++;
                    }
                    continue;
                }

                if (c == '{')
                {
                    int close = segment.IndexOf('}', i + 1);
                    if (close < 0)
                    {
                        sb.Append(Regex.Escape(c.ToString()));
                        i++;
                        continue;
                    }

                    var typeName = segment.Substring(i + 1, close - i - 1);
                    if (!customParameterTypeRegexFragments.TryGetValue(typeName, out var fragment))
                    {
                        throw new InvalidOperationException(
                            $"Unknown Cucumber Expression parameter type '{{{typeName}}}' - no registered regex fragment supplied.");
                    }

                    sb.Append("(?:").Append(fragment).Append(')');
                    i = close + 1;
                    continue;
                }

                if (c == '(')
                {
                    int close = FindMatchingUnescapedParen(segment, i);
                    if (close < 0)
                    {
                        sb.Append(Regex.Escape(c.ToString()));
                        i++;
                        continue;
                    }

                    var inner = segment.Substring(i + 1, close - i - 1);
                    sb.Append("(?:").Append(CompileSegment(inner, customParameterTypeRegexFragments)).Append(")?");
                    i = close + 1;
                    continue;
                }

                if (!char.IsWhiteSpace(c))
                {
                    int wordEnd = FindWordRunEnd(segment, i);
                    var word = segment.Substring(i, wordEnd - i);
                    if (word.Contains('/'))
                    {
                        var alternatives = word.Split('/').Select(Regex.Escape);
                        sb.Append("(?:").Append(string.Join("|", alternatives)).Append(')');
                    }
                    else
                    {
                        sb.Append(Regex.Escape(word));
                    }

                    i = wordEnd;
                    continue;
                }

                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Finds the end (exclusive) of the maximal run of "word" characters starting at
        /// <paramref name="startIndex"/> - a run of non-whitespace characters that stops at
        /// the next whitespace or the next unescaped '(' , '{' , or '\'. This is the unit
        /// Cucumber Expressions use for '/' alternation: "have/had" is one such run
        /// containing two alternatives ("have", "had"); a run with no '/' is plain literal
        /// text (still returned as one run so multi-char literal spans are escaped together,
        /// which is behaviourally identical to escaping char-by-char).
        /// </summary>
        private static int FindWordRunEnd(string segment, int startIndex)
        {
            int j = startIndex;
            while (j < segment.Length)
            {
                char c = segment[j];
                if (char.IsWhiteSpace(c) || c == '(' || c == '{' || c == '\\') break;
                j++;
            }

            return j;
        }

        /// <summary>
        /// Finds the next unescaped ')' after an unescaped '(' at <paramref name="openIndex"/>.
        /// No nesting support - real-world patterns seen in this repo never nest optional-text
        /// groups, and Cucumber Expressions don't support nesting either.
        /// </summary>
        private static int FindMatchingUnescapedParen(string segment, int openIndex)
        {
            for (int j = openIndex + 1; j < segment.Length; j++)
            {
                if (segment[j] == '\\' && j + 1 < segment.Length)
                {
                    j++;
                    continue;
                }

                if (segment[j] == ')') return j;
            }

            return -1;
        }

        /// <summary>
        /// Builds the regex fragment for an enum-auto-registered `{EnumTypeName}`
        /// placeholder: an alternation of every member name, wrapped so it matches
        /// case-insensitively (per the confirmed Reqnroll enum-auto-registration rule),
        /// without forcing case-insensitivity on the rest of the compiled pattern.
        /// </summary>
        public static string EnumFragment(Type enumType)
        {
            if (enumType is null) throw new ArgumentNullException(nameof(enumType));
            if (!enumType.IsEnum) throw new ArgumentException($"{enumType.FullName} is not an enum type.", nameof(enumType));

            var alternation = string.Join("|", Enum.GetNames(enumType).Select(Regex.Escape));
            return $"(?i:{alternation})";
        }
    }
}
