// Pure, unit-testable method-name-convention pattern generator, extracted from the gap
// in Program.cs: when a [Given]/[When]/[Then] method has NO explicit pattern string,
// Program.cs records it with pattern "<method-name-convention>" and a note "no explicit
// pattern - method-name convention not evaluated by this spike" (see ScanAssemblyForBindings
// in Program.cs) and EXCLUDES it from the dead/alive verdict UNCONDITIONALLY - regardless
// of whether the method actually has zero parameters (in which case Reqnroll DOES bind it
// via the method-name convention) or one-or-more parameters (in which case Reqnroll never
// binds it at all under bare convention). Blanket-excluding the zero-parameter case is a
// real coverage gap: those bindings could be correctly evaluated as alive/dead, but today
// they never contribute to either bucket.
//
// Confirmed ground truth (see scratchpad/reqnroll-quirks-verification/):
//   - Method-name-convention (no explicit pattern string): PascalCase method name
//     converts to a literal, case-insensitive text pattern.
//   - Tolerates a trailing period on the step text.
//   - Requires an exact word match otherwise (no partial/substring binding - same
//     full-match rule as everywhere else, see MatchingLogic.IsFullMatch).
//   - NEVER supports parameter capture - a parameterized method under bare convention
//     simply never binds, at all.
using System.Text.RegularExpressions;

namespace SpecHygiene.Analysis.Reqnroll
{
    public static class MethodNameConvention
    {
        private static readonly HashSet<string> KeywordPrefixWords =
            new(StringComparer.OrdinalIgnoreCase) { "Given", "When", "Then", "And", "But" };

        private static readonly Regex PascalWordRegex = new(@"[A-Z][a-z0-9]*|[A-Z]+(?![a-z])", RegexOptions.Compiled);

        /// <summary>
        /// Converts a PascalCase step-binding method name into the literal, space-separated
        /// text pattern Reqnroll's method-name convention would derive from it - or null if
        /// convention binding is impossible for this method (parameterCount &gt; 0, per the
        /// confirmed rule that convention-only bindings never support parameter capture).
        /// The leading Given/When/Then/And/But prefix word is stripped first since it is not
        /// part of the step text itself.
        /// </summary>
        /// <returns>
        /// A literal (non-regex) pattern such as "the user is logged in", or null when this
        /// method cannot be bound at all under bare convention. Use
        /// <see cref="CompileConventionRegex"/> to turn a non-null result into a matchable
        /// Regex (case-insensitive, tolerant of one trailing period, full-match only).
        /// </returns>
        public static string? GenerateConventionPattern(string methodName, int parameterCount)
        {
            if (parameterCount > 0)
            {
                return null;
            }

            if (string.IsNullOrEmpty(methodName))
            {
                return null;
            }

            var words = PascalWordRegex.Matches(methodName).Select(m => m.Value).ToList();
            if (words.Count > 1 && KeywordPrefixWords.Contains(words[0]))
            {
                words.RemoveAt(0);
            }

            return string.Join(" ", words);
        }

        /// <summary>
        /// Compiles a literal pattern produced by <see cref="GenerateConventionPattern"/> into
        /// the Regex Reqnroll would actually match a step's text against: case-insensitive,
        /// full-match only, tolerating exactly one optional trailing period on the step text.
        /// </summary>
        public static Regex CompileConventionRegex(string literalPattern)
        {
            if (literalPattern is null) throw new ArgumentNullException(nameof(literalPattern));
            return new Regex("^" + Regex.Escape(literalPattern) + @"\.?$", RegexOptions.IgnoreCase);
        }
    }
}
