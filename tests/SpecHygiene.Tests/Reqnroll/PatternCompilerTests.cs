using SpecHygiene.Analysis.Reqnroll;
using Xunit;
// TDD test-oracle for DeadStepFinder.PatternCompiler, covering the confirmed
// enum-auto-registration ground truth: a `{EnumTypeName}` placeholder matches any of
// that enum's member names, CASE-INSENSITIVELY, regardless of namespace.
using System.Text.RegularExpressions;

namespace SpecHygiene.Tests.Reqnroll
{
    // Deliberately declared in a namespace/location unrelated to Reqnroll or the real
    // repo's enums, to prove the "regardless of namespace" part of the confirmed rule -
    // PatternCompiler.EnumFragment only cares about member names via reflection.
    public enum FakeTestEnum
    {
        Alpha,
        Beta,
        GammaRay,
    }

    public class PatternCompilerTests
    {
        [Fact]
        public void CompilePattern_UnknownPlaceholder_Throws()
        {
            var fragments = new Dictionary<string, string>();

            Assert.Throws<InvalidOperationException>(() =>
                PatternCompiler.CompilePattern("I select {FakeTestEnum} option", fragments));
        }

        [Theory]
        [InlineData("I select Alpha option", true)]
        [InlineData("I select alpha option", true)]   // case-insensitive per confirmed rule
        [InlineData("I select ALPHA option", true)]
        [InlineData("I select gammaray option", true)]
        [InlineData("I select Delta option", false)]  // not a member of the enum
        [InlineData("I select Alpha option extra", false)] // full-match still required
        public void CompilePattern_EnumPlaceholder_MatchesMemberNamesCaseInsensitively(string stepText, bool expectedMatch)
        {
            var fragments = new Dictionary<string, string>
            {
                ["FakeTestEnum"] = PatternCompiler.EnumFragment(typeof(FakeTestEnum)),
            };

            var regex = PatternCompiler.CompilePattern("I select {FakeTestEnum} option", fragments);

            Assert.Equal(expectedMatch, MatchingLogic.IsFullMatch(regex, stepText));
        }

        [Fact]
        public void CompilePattern_LiteralTextOutsidePlaceholders_IsCaseSensitive()
        {
            // Only the enum placeholder itself is case-insensitive per the confirmed rule -
            // surrounding literal text is not blanket-lowered/case-folded.
            var fragments = new Dictionary<string, string>
            {
                ["FakeTestEnum"] = PatternCompiler.EnumFragment(typeof(FakeTestEnum)),
            };

            var regex = PatternCompiler.CompilePattern("I select {FakeTestEnum} option", fragments);

            Assert.False(MatchingLogic.IsFullMatch(regex, "I SELECT Alpha option"));
        }

        [Fact]
        public void CompilePattern_StepArgumentTransformationFragment_UsesGivenRegexVerbatim()
        {
            // [StepArgumentTransformation(regex, Name = "x")] registers a {x} placeholder
            // using the given regex as-is (no forced case-insensitivity).
            var fragments = new Dictionary<string, string>
            {
                ["policyRef"] = @"POL-\d{6}",
            };

            var regex = PatternCompiler.CompilePattern("I look up policy {policyRef}", fragments);

            Assert.True(MatchingLogic.IsFullMatch(regex, "I look up policy POL-123456"));
            Assert.False(MatchingLogic.IsFullMatch(regex, "I look up policy pol-123456")); // verbatim, case-sensitive
            Assert.False(MatchingLogic.IsFullMatch(regex, "I look up policy POL-123456 today")); // full-match required
        }

        // ---------------------------------------------------------------------------
        // Confirmed production regression (2026-07-07): PatternCompiler currently
        // Regex.Escape()s EVERYTHING outside a {placeholder}, including '(' ')' '/'.
        // Real Cucumber Expressions give these characters special meaning:
        //   - unescaped "(text)" is OPTIONAL TEXT - matches with or without "text".
        //   - "\(" "\)" "\/" are escapes for LITERAL '(' ')' '/'.
        // Because PatternCompiler does neither, any [Binding] pattern classified as a
        // Cucumber Expression that contains '(' ')' or '/' outside a placeholder
        // compiles to a regex that can never match its real, live step text - a
        // confirmed false "dead" verdict. This is exactly what dead-step-finder's own
        // HasSyntaxGapSyntax diagnostic flags (IsCucumberExpression && pattern contains
        // '(' or '/') but the flag was informational only - the dead/alive verdict
        // still used the broken regex. Found via real deletions in Acme.API.DataSet,
        // Acme.API.Estimate that turned out to still be live in production.
        // ---------------------------------------------------------------------------

        [Theory]
        [InlineData("I select (optional )apples", "I select optional apples", true)]
        [InlineData("I select (optional )apples", "I select apples", true)]
        [InlineData("I select (optional )apples", "I select something apples", false)]
        public void CompilePattern_UnescapedParens_AreOptionalText(string pattern, string stepText, bool expectedMatch)
        {
            var regex = PatternCompiler.CompilePattern(pattern, new Dictionary<string, string>());

            Assert.Equal(expectedMatch, MatchingLogic.IsFullMatch(regex, stepText));
        }

        [Theory]
        [InlineData(@"user should see {int} expected rate\(s\) in the sections", "user should see 3 expected rate(s) in the sections", true)]
        [InlineData(@"user should see {int} expected rate\(s\) in the sections", "user should see 3 expected rate in the sections", false)]
        public void CompilePattern_EscapedParens_AreLiteralCharacters(string pattern, string stepText, bool expectedMatch)
        {
            var fragments = new Dictionary<string, string>
            {
                ["int"] = @"-?\d+",
            };

            var regex = PatternCompiler.CompilePattern(pattern, fragments);

            Assert.Equal(expectedMatch, MatchingLogic.IsFullMatch(regex, stepText));
        }

        [Fact]
        public void CompilePattern_ProductionRegression_EscapedParensReleaseTechnicallyCorrect()
        {
            // Acme.API.Estimate.Steps.EstimateSteps.WhenTheUserRevisesTheEstimateAndReleasesAsTechnicallyCorrectOKStatus
            var regex = PatternCompiler.CompilePattern(
                @"the user revises the estimate and Releases as Technically Correct \(OK status\)",
                new Dictionary<string, string>());

            Assert.True(MatchingLogic.IsFullMatch(regex, "the user revises the estimate and Releases as Technically Correct (OK status)"));
        }

        [Fact]
        public void CompilePattern_ProductionRegression_EscapedParensWithoutLeasingDetails()
        {
            // Acme.API.DataSet.Steps.DataSetPoliciesSteps.WhenAnIntegrationServiceAPICallIsMadeToCreateThatPolicyWithoutLeasingDetails
            var regex = PatternCompiler.CompilePattern(
                @"an Integration service API call is made to create that policy \(without leasing details\)",
                new Dictionary<string, string>());

            Assert.True(MatchingLogic.IsFullMatch(regex, "an Integration service API call is made to create that policy (without leasing details)"));
        }

        [Fact]
        public void CompilePattern_ProductionRegression_LeadingOptionalTextRegexFlagLookalike()
        {
            // Acme.API.Estimate.Steps.EstimateSteps.WhenUserProcessesReviseEstimateBffBySupplier.
            // "(?i)" was written meaning "regex case-insensitive flag", but Reqnroll classified
            // this pattern as a Cucumber Expression, where unescaped "(?i)" is optional text
            // "?i" - matches with the literal "?i" prefix present OR (as in the real step) absent.
            var regex = PatternCompiler.CompilePattern(
                "(?i)user processes revise estimate bff by supplier",
                new Dictionary<string, string>());

            Assert.True(MatchingLogic.IsFullMatch(regex, "user processes revise estimate bff by supplier"));
            Assert.True(MatchingLogic.IsFullMatch(regex, "?iuser processes revise estimate bff by supplier"));
        }

        // ---------------------------------------------------------------------------
        // Still-open half of the same known gap (2026-07-09): the optional-text "(...)"
        // half was fixed above, but '/' ALTERNATION was never implemented - CompileSegment
        // has no case for '/' at all, so it falls through to the default branch and gets
        // Regex.Escape()'d as a plain literal character. Real Cucumber Expressions treat
        // unescaped '/' as alternative text: "have/had" matches "have" OR "had", where each
        // alternative is the maximal run of non-whitespace, non-{, non-(, non-\ characters
        // immediately touching the '/' (chained slashes - "a/b/c" - all become one
        // alternation of 3). This is exactly what HasSyntaxGapSyntax's own comment already
        // named ("Cucumber Expression optional-text/alternation syntax") but only the
        // optional-text half had a fix; alternation compiled to a dead-on-arrival literal
        // regex for any live Cucumber-Expression-classified binding using it (confirmed
        // manually for Acme.UI.Tests.Steps.GlobalConfig.Communications.EmailSmsCommsPageSteps'
        // "I filter the Email/SMS results grid" pattern during Phase 4 review, 2026-07-09).
        // ---------------------------------------------------------------------------

        [Theory]
        [InlineData("I have/had a coin", "I have a coin", true)]
        [InlineData("I have/had a coin", "I had a coin", true)]
        [InlineData("I have/had a coin", "I have/had a coin", false)] // literal slash text no longer matches once treated as alternation
        [InlineData("I have/had a coin", "I haveorhad a coin", false)]
        [InlineData("a/b/c option selected", "a option selected", true)]     // 3-way chained alternation
        [InlineData("a/b/c option selected", "b option selected", true)]
        [InlineData("a/b/c option selected", "c option selected", true)]
        [InlineData("a/b/c option selected", "d option selected", false)]
        public void CompilePattern_UnescapedSlash_IsAlternativeText(string pattern, string stepText, bool expectedMatch)
        {
            var regex = PatternCompiler.CompilePattern(pattern, new Dictionary<string, string>());

            Assert.Equal(expectedMatch, MatchingLogic.IsFullMatch(regex, stepText));
        }

        [Fact]
        public void CompilePattern_ProductionRegression_EmailSlashSmsCommsTemplates()
        {
            // Acme.UI.Tests.Steps.GlobalConfig.Communications.EmailSmsCommsPageSteps.
            // WhenIFilterTheEmailSmsResultsGrid - confirmed genuinely dead (no feature file
            // references either interpretation), but grounds this fix in the real pattern
            // shape that motivated it rather than a purely synthetic example.
            var regex = PatternCompiler.CompilePattern(
                "I filter the Email/SMS results grid",
                new Dictionary<string, string>());

            Assert.True(MatchingLogic.IsFullMatch(regex, "I filter the Email results grid"));
            Assert.True(MatchingLogic.IsFullMatch(regex, "I filter the SMS results grid"));
            Assert.False(MatchingLogic.IsFullMatch(regex, "I filter the Email/SMS results grid"));
        }

        [Theory]
        [InlineData(@"I select\/deselect the item", "I select/deselect the item", true)]
        [InlineData(@"I select\/deselect the item", "I select the item", false)]
        public void CompilePattern_EscapedSlash_IsLiteralCharacter(string pattern, string stepText, bool expectedMatch)
        {
            var regex = PatternCompiler.CompilePattern(pattern, new Dictionary<string, string>());

            Assert.Equal(expectedMatch, MatchingLogic.IsFullMatch(regex, stepText));
        }

        // ---------------------------------------------------------------------------
        // Regression lock-in (2026-07-17): a parenthesised group containing a PIPE -
        // e.g. "(Order|OrderItem)" - is NOT regex alternation in a Cucumber Expression.
        // '(' ')' are optional-text delimiters and '|' is an ordinary literal character,
        // so Reqnroll compiles "(Order|OrderItem)" to OPTIONAL TEXT of the literal string
        // "Order|OrderItem" - matching either "Order|OrderItem" or "" (absent), but NEVER
        // "Order" or "OrderItem" on their own.
        //
        // This looks like a tool false positive ("the author clearly meant alternation, so
        // marking it dead must be a bug") and was recorded as one during Phase 3 review
        // (Acme.API.OrderProcessor.Steps.CommentSteps.
        // ThenTheCommentListShouldContainThePostedGpmStructuredCommentViaIntegration, pattern
        // "the comment list for the '(Order|OrderItem)' should contain the posted Acme
        // structured comment"). It is NOT a tool bug. Verified empirically against the real
        // Reqnroll 3.3.3 / CucumberExpressions 17.1.0 stack: its own CucumberExpressionDetector
        // classifies the pattern as a Cucumber Expression (not a regex), and the real
        // CucumberExpression compiles it to the IDENTICAL regex this tool produces
        // (^...'(?:Order\|OrderItem)?'...$), which does not match 'Order' or 'OrderItem'.
        // The binding is genuinely dead as written - the author's regex-alternation intent
        // silently does nothing under Cucumber Expression semantics.
        //
        // DO NOT "fix" PatternCompiler to treat "(a|b)" as alternation: that would make this
        // tool DISAGREE with Reqnroll (report a genuinely-dead binding as alive), which is a
        // worse bug than the surprising-but-faithful behaviour locked in here.
        // ---------------------------------------------------------------------------

        [Theory]
        [InlineData("the comment list for the '(Order|OrderItem)' should contain the posted Acme structured comment",
                    "the comment list for the 'Order|OrderItem' should contain the posted Acme structured comment", true)]
        [InlineData("the comment list for the '(Order|OrderItem)' should contain the posted Acme structured comment",
                    "the comment list for the '' should contain the posted Acme structured comment", true)]
        [InlineData("the comment list for the '(Order|OrderItem)' should contain the posted Acme structured comment",
                    "the comment list for the 'Order' should contain the posted Acme structured comment", false)]
        [InlineData("the comment list for the '(Order|OrderItem)' should contain the posted Acme structured comment",
                    "the comment list for the 'OrderItem' should contain the posted Acme structured comment", false)]
        public void CompilePattern_ParenPipeGroup_IsOptionalLiteralNotAlternation_MatchesReqnroll(string pattern, string stepText, bool expectedMatch)
        {
            var regex = PatternCompiler.CompilePattern(pattern, new Dictionary<string, string>());

            Assert.Equal(expectedMatch, MatchingLogic.IsFullMatch(regex, stepText));
        }

        [Fact]
        public void CompilePattern_ParenPipeGroup_CompilesToOptionalLiteralFragmentNotAlternation()
        {
            // Structural lock-in on the one decision that matters: the "(Order|OrderItem)"
            // group must compile to the optional-LITERAL fragment "(?:Order\|OrderItem)?"
            // (pipe escaped, whole group optional), exactly as real Reqnroll does - NOT to a
            // regex alternation "(?:Order|OrderItem)".
            //
            // We assert on the fragment rather than the whole regex string on purpose: this
            // tool escapes literal spaces via Regex.Escape ("\ ") while Reqnroll leaves them
            // bare (" "). Those are regex-equivalent (both match a space), so the compiled
            // patterns behave identically - proven by the Theory above - but are not
            // byte-identical strings. The fragment is the meaningful, escaping-independent part.
            var regex = PatternCompiler.CompilePattern(
                "the comment list for the '(Order|OrderItem)' should contain the posted Acme structured comment",
                new Dictionary<string, string>());

            Assert.Contains(@"(?:Order\|OrderItem)?", regex.ToString());          // optional literal, pipe escaped
            Assert.DoesNotContain("(?:Order|OrderItem)", regex.ToString());        // NOT alternation
        }
    }
}
