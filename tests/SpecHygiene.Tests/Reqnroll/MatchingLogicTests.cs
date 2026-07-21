using SpecHygiene.Analysis.Reqnroll;
using Xunit;
// TDD test-oracle for DeadStepFinder.MatchingLogic.IsFullMatch.
//
// Ground truth (empirically confirmed against real Reqnroll via a separate throwaway
// Reqnroll+NUnit verification project - see
// scratchpad/reqnroll-quirks-verification/ - NOT re-derived here):
//   Reqnroll always requires a FULL match of the whole step text. It NEVER accepts a
//   partial/substring match, with or without explicit ^ / $ anchors in the source
//   pattern. The original tool's `Regex.IsMatch()` call allows substring matches, which
//   is a confirmed real false-positive bug (found via production logs) - a binding
//   pattern like "I do something" gets reported ALIVE against an unrelated step
//   "I do something extra".
using System.Text.RegularExpressions;

namespace SpecHygiene.Tests.Reqnroll
{
    public class MatchingLogicTests
    {
        // --- Sanity check: documents the PRE-EXISTING bug in the original tool's approach. ---
        // This test targets the naive .NET Regex.IsMatch() API directly (NOT our fixed
        // IsFullMatch function) to prove the bug is real and not a misunderstanding of Regex
        // semantics. It will keep passing forever (it is not itself a regression test for our
        // fix) - it is executable documentation of *why* the fix is needed.
        [Fact]
        public void NaiveRegexIsMatch_IncorrectlyAllowsSubstringMatch_DemonstratesTheBug()
        {
            var pattern = new Regex("I do something");
            var stepText = "I do something extra";

            // This is TRUE - proving plain IsMatch() is the wrong tool for step matching.
            Assert.True(pattern.IsMatch(stepText));
        }

        // --- The critical, highest-value test (task priority #1). ---
        [Fact]
        public void IsFullMatch_RejectsUnanchoredPatternAgainstLongerStepText()
        {
            var pattern = new Regex("I do something");
            var stepText = "I do something extra";

            Assert.False(MatchingLogic.IsFullMatch(pattern, stepText));
        }

        [Fact]
        public void IsFullMatch_AcceptsExactUnanchoredMatch()
        {
            var pattern = new Regex("I do something");
            var stepText = "I do something";

            Assert.True(MatchingLogic.IsFullMatch(pattern, stepText));
        }

        [Fact]
        public void IsFullMatch_RejectsMatchThatIsOnlyASuffix()
        {
            // Match exists but doesn't start at index 0 - must still be rejected.
            var pattern = new Regex("something extra");
            var stepText = "I do something extra";

            Assert.False(MatchingLogic.IsFullMatch(pattern, stepText));
        }

        [Fact]
        public void IsFullMatch_RejectsMatchThatIsOnlyAPrefix()
        {
            // Match exists at index 0 but doesn't consume the whole string.
            var pattern = new Regex("I do something");
            var stepText = "I do something else entirely";

            Assert.False(MatchingLogic.IsFullMatch(pattern, stepText));
        }

        [Theory]
        [InlineData("I do something", true)]
        [InlineData("I do something extra", false)]
        [InlineData("I do", false)]
        public void IsFullMatch_WorksCorrectlyEvenWithExplicitAnchors(string stepText, bool expected)
        {
            // Confirmed ground truth: full-match is ALWAYS required, regardless of whether
            // the pattern already carries explicit ^ / $ anchors. An anchored pattern should
            // behave identically through IsFullMatch as an unanchored one.
            var pattern = new Regex("^I do something$");

            Assert.Equal(expected, MatchingLogic.IsFullMatch(pattern, stepText));
        }

        [Theory]
        [InlineData("I have 42 cukes", true)]
        [InlineData("I have 42 cukes today", false)]
        [InlineData("well, I have 42 cukes", false)]
        public void IsFullMatch_WorksWithCapturingGroupsLikeCucumberExpressionsProduce(string stepText, bool expected)
        {
            var pattern = new Regex(@"I have (\d+) cukes");

            Assert.Equal(expected, MatchingLogic.IsFullMatch(pattern, stepText));
        }
    }

    // TDD test-oracle for DeadStepFinder.MatchingLogic.IsReqnrollRegexMatch.
    //
    // Ground truth (empirically confirmed against real Reqnroll via Q7/Q8 in the throwaway
    // Reqnroll+NUnit verification project - scratchpad/reqnroll-quirks-verification/ - and
    // cross-checked against Reqnroll's real source, Reqnroll.Bindings.CucumberExpressions.
    // CucumberExpressionDetector, on 2026-07-08 - NOT re-derived here):
    //   For a raw-regex-classified step pattern, Reqnroll prepends '^' only if the pattern
    //   doesn't already textually start with it, and appends '$' only if it doesn't already
    //   textually end with it - then does a bare IsMatch with the result. It does NOT verify
    //   the anchors actually apply to the WHOLE expression. IsFullMatch above (index==0 &&
    //   length==full) is too strict for this: it produced a confirmed false-DEAD verdict for
    //   OrderTaskSteps.ThenUserUpdatesManualTask's
    //   "^user updates and completes|cancels manual task$" attribute in Acme.API.Motor,
    //   which real Reqnroll binds successfully.
    public class MatchingLogicReqnrollRegexMatchTests
    {
        [Theory]
        [InlineData("I do something", "I do something extra unexpected", false)] // Q8a: no anchors, longer text -> rejected
        [InlineData("I do something", "I do something", true)]                    // exact unanchored match still works
        [InlineData("^I do a start anchored thing", "I do a start anchored thing extra unexpected", false)] // Q8b
        [InlineData("I do an end anchored thing$", "extra unexpected I do an end anchored thing", false)]   // Q8c
        [InlineData("^I do a fully anchored thing$", "I do a fully anchored thing", true)]                  // Q8d sanity check
        public void MatchesEmpiricallyConfirmedAnchorWrappingBehavior(string pattern, string stepText, bool expected)
        {
            Assert.Equal(expected, MatchingLogic.IsReqnrollRegexMatch(pattern, stepText));
        }

        [Fact]
        public void AcceptsUnparenthesizedAlternationWithTextuallyPresentAnchors_EvenThoughTheyDontApplyToBothBranches()
        {
            // Q7: the pattern textually starts with '^' and ends with '$', so Reqnroll trusts
            // it as "already anchored" and never wraps it - even though, due to `|`
            // precedence, '^' only binds the first alternative and '$' only binds the second.
            // Reqnroll then falls back to a bare, boundary-free substring match, so a step
            // text that is merely the first alternative (plus unrelated trailing text) still
            // binds.
            var pattern = "^user updates and completes|cancels manual task$";

            Assert.True(MatchingLogic.IsReqnrollRegexMatch(pattern, "user updates and completes manual task"));
            Assert.True(MatchingLogic.IsReqnrollRegexMatch(pattern, "user updates and cancels manual task"));
        }

        [Fact]
        public void StillRejectsCompletelyUnrelatedTextEvenWithTheAlternationQuirk()
        {
            // Guards against a fix that just returns true unconditionally for any
            // alternation pattern - neither branch appears anywhere in this text.
            var pattern = "^user updates and completes|cancels manual task$";

            Assert.False(MatchingLogic.IsReqnrollRegexMatch(pattern, "some totally unrelated step"));
        }
    }
}
