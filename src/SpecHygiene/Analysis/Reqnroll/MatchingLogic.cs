// Pure, unit-testable step-matching logic extracted from Program.cs.
//
// Program.cs (the repo-wide spike tool) currently decides "does this binding's compiled
// Regex match this feature step's text?" via a bare `Regex.IsMatch()` call (see
// `SafeIsMatch` in Program.cs). That is WRONG: it allows partial/substring matches,
// which is a real, confirmed false-positive source (a binding pattern like
// "I do something" would be reported ALIVE against an unrelated step
// "I do something extra", when Reqnroll itself would never bind them together).
//
// Ground truth below was confirmed empirically against real Reqnroll (see the
// throwaway Reqnroll+NUnit verification project at
// scratchpad/reqnroll-quirks-verification/), not assumed:
//   - Reqnroll's step matcher ALWAYS requires a full-text match. It never accepts a
//     partial/substring match, regardless of whether the source pattern carries
//     explicit ^ / $ anchors or not.
//
// This file is intentionally free of any dependency on Reqnroll/CucumberExpressions
// types so it (and its tests) can be exercised in complete isolation from the rest of
// the spike tool and from the real repo.
using System.Text.RegularExpressions;

namespace SpecHygiene.Analysis.Reqnroll
{
    public static class MatchingLogic
    {
        /// <summary>
        /// True only if <paramref name="compiledPattern"/> matches <paramref name="stepText"/>
        /// in full - the match starts at index 0 and consumes the entire string. Correct for
        /// bindings whose Regex was produced by PatternCompiler.CompilePattern (Cucumber
        /// Expression path): that path always wraps the ENTIRE translated expression in
        /// ^...$, so the anchors unambiguously apply to the whole pattern and this simple
        /// index/length check is equivalent to Reqnroll's real behavior. Do NOT use this for
        /// raw-regex-classified bindings (see IsReqnrollRegexMatch below) - a pattern with a
        /// top-level unparenthesized `|` can carry ^ and $ that each apply to only ONE
        /// alternative, and this function would still (correctly, for what it checks) require
        /// the match to span the full text - which is NOT what Reqnroll actually does for that
        /// pattern shape at runtime.
        /// </summary>
        public static bool IsFullMatch(Regex compiledPattern, string stepText)
        {
            if (compiledPattern is null) throw new ArgumentNullException(nameof(compiledPattern));
            if (stepText is null) throw new ArgumentNullException(nameof(stepText));

            var match = compiledPattern.Match(stepText);
            return match.Success && match.Index == 0 && match.Length == stepText.Length;
        }

        /// <summary>
        /// Replicates Reqnroll's actual runtime matching for a raw-regex-classified step
        /// definition pattern (i.e. CucumberExpressionDetector.IsCucumberExpression returned
        /// false for it - see Reqnroll.Bindings.CucumberExpressions.CucumberExpressionDetector,
        /// which treats a pattern as regex whenever it starts with '^' or ends with '$').
        ///
        /// Ground truth confirmed empirically against real Reqnroll (Q7/Q8 in
        /// scratchpad/reqnroll-quirks-verification/, 2026-07-08 - not assumed):
        /// Reqnroll does NOT verify that a match spans the whole step text via a general
        /// index/length check on the pattern as originally written. Instead it prepends '^'
        /// only if the pattern doesn't already textually start with it, and appends '$' only
        /// if it doesn't already textually end with it, then does a bare, unmodified IsMatch
        /// with the result. Critically, this wrap-if-missing check is purely TEXTUAL
        /// (StartsWith("^") / EndsWith("$") on the original string) - it does NOT verify the
        /// anchors actually apply to the WHOLE expression. A pattern with a top-level
        /// unparenthesized `|`, e.g. "^A|B$", textually starts with '^' and ends with '$' so
        /// Reqnroll leaves it completely unwrapped and matches it via bare IsMatch - even
        /// though the '^' only binds to the "A" branch and the '$' only binds to the "B"
        /// branch, so a step text that is merely "A" followed by other trailing text still
        /// binds (matches the "^A" branch as a plain, boundary-free substring match).
        ///
        /// This was the exact root cause of a false-DEAD verdict for
        /// OrderTaskSteps.ThenUserUpdatesManualTask's
        /// "^user updates and completes|cancels manual task$" attribute in Acme.Api.Motor -
        /// confirmed dead by the previous (too strict) IsFullMatch check, but confirmed ALIVE
        /// by a real Reqnroll+NUnit run.
        /// </summary>
        public static bool IsReqnrollRegexMatch(string rawPattern, string stepText)
        {
            if (rawPattern is null) throw new ArgumentNullException(nameof(rawPattern));
            if (stepText is null) throw new ArgumentNullException(nameof(stepText));

            return new Regex(EffectiveRegexPattern(rawPattern)).IsMatch(stepText);
        }

        /// <summary>
        /// The pattern Reqnroll actually matches a raw-regex binding with: '^' prepended and '$'
        /// appended ONLY when textually absent (see IsReqnrollRegexMatch for why that "textually"
        /// matters). Extracted so a caller can compile it ONCE and reuse the compiled Regex across
        /// many step texts — constructing a new Regex per match, as the one-shot form does, is
        /// pathologically slow in the analyzer's step×binding loop.
        /// </summary>
        public static string EffectiveRegexPattern(string rawPattern)
        {
            string effective = rawPattern;
            if (!effective.StartsWith("^")) effective = "^" + effective;
            if (!effective.EndsWith("$")) effective = effective + "$";
            return effective;
        }
    }
}
