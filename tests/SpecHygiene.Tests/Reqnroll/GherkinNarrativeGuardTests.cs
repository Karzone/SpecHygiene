using SpecHygiene.Analysis.Reqnroll;
using Xunit;
// TDD test-oracle for DeadStepFinder.GherkinNarrativeGuard - see that file's header for the
// confirmed production regression this closes.
namespace SpecHygiene.Tests.Reqnroll
{
    public class GherkinNarrativeGuardTests
    {
        [Fact]
        public void ComputeInsideBlockFlags_ProductionRegression_PollSearchAndRetrieve()
        {
            var lines = new[]
            {
                "@Acme",                                              // 0
                "Feature: PollSearchAndRetrieve",                    // 1
                "",                                                  // 2
                "\tAcme Integration Polling mechanism tests for:",    // 3
                "\t\tSearch   verifies response body",               // 4
                "\t\t\t\t\t\t   and compares against outbound payload", // 5 - narrative, not a step
                "",                                                  // 6
                "@Acme-18484",                                        // 7
                "Scenario: Acme_POLL_001",                            // 8
                "\tGiven a thing",                                   // 9
                "\tAnd compares against outbound payload",           // 10 - real step, same text
            };

            var flags = GherkinNarrativeGuard.ComputeInsideBlockFlags(lines);

            Assert.False(flags[5], "narrative continuation line before any Background/Scenario header must not be treated as inside a block");
            Assert.True(flags[10], "an And line inside a real Scenario block must be treated as inside a block");
        }

        [Fact]
        public void ComputeInsideBlockFlags_ProductionRegression_EstimateOnlyInvoices()
        {
            var lines = new[]
            {
                "Feature: Add not allowed Additional Invoices in Estimate Only", // 0
                "",                                                              // 1
                "Add not allowed Additional Invoices in an EO order",            // 2
                "and check for the appropriate error.",                          // 3 - narrative, not a step
                "",                                                              // 4
                "Background:",                                                  // 5
                "\tGiven that the user locates a supplier with service type",    // 6
            };

            var flags = GherkinNarrativeGuard.ComputeInsideBlockFlags(lines);

            Assert.False(flags[3]);
            Assert.True(flags[6]);
        }

        [Fact]
        public void ComputeInsideBlockFlags_NoHeaderAtAll_EveryLineFalse()
        {
            var lines = new[] { "Feature: X", "", "some narrative and more text" };

            var flags = GherkinNarrativeGuard.ComputeInsideBlockFlags(lines);

            Assert.All(flags, Assert.False);
        }

        [Fact]
        public void ComputeInsideBlockFlags_HeaderLineItself_IsInsideBlock()
        {
            var lines = new[] { "Feature: X", "Background:", "\tGiven a thing" };

            var flags = GherkinNarrativeGuard.ComputeInsideBlockFlags(lines);

            Assert.False(flags[0]);
            Assert.True(flags[1]);
            Assert.True(flags[2]);
        }

        // Confirmed empirically (2026-07-09, throwaway probe project referencing the real
        // `Gherkin` NuGet package Reqnroll itself depends on): step keywords are matched
        // case-sensitively by the real parser. A line starting with lowercase "and"/"when"/etc.
        // is not recognized as a step keyword at all - before a block's first real step it's
        // free-text description (Gherkin.Ast Scenario.Description swallows it whole), and after
        // the first real step it would be an actual parse error, never silently treated as a
        // step. Confirmed zero real steps in this repo rely on a lowercase keyword - the only 5
        // lowercase hits found repo-wide are all pre-Background/Scenario Feature narrative,
        // already excluded by ComputeInsideBlockFlags above - so this is a pure precision fix
        // with no regression risk. Production case: Acme.API.E2E's "Convert Estimate Only to
        // Vehicle Repair with Invoices.feature" line 304, "and manually crediting the
        // Additional Estimation Fee, and then converts from EO to VR Comfort." - Scenario
        // description text (before the first real "When" step), wrongly parsed as an undefined
        // Given step because the regex was case-insensitive.
        [Fact]
        public void TryMatchStepLine_ProductionRegression_ConvertEstimateOnlyToVehicleRepair()
        {
            var matched = GherkinNarrativeGuard.TryMatchStepLine(
                "\tand manually crediting the Additional Estimation Fee, and then converts from EO to VR Comfort.",
                out _, out _);

            Assert.False(matched, "lowercase 'and' narrative continuation text must never be treated as a step line");
        }

        [Theory]
        [InlineData("given")]
        [InlineData("when")]
        [InlineData("then")]
        [InlineData("and")]
        [InlineData("but")]
        public void TryMatchStepLine_LowercaseKeywords_DoNotMatch(string lowercaseKeyword)
        {
            var matched = GherkinNarrativeGuard.TryMatchStepLine($"\t{lowercaseKeyword} something happens", out _, out _);

            Assert.False(matched);
        }

        [Theory]
        [InlineData("Given")]
        [InlineData("When")]
        [InlineData("Then")]
        [InlineData("And")]
        [InlineData("But")]
        public void TryMatchStepLine_ProperlyCapitalizedKeywords_Match(string keyword)
        {
            var matched = GherkinNarrativeGuard.TryMatchStepLine($"\t{keyword} something happens", out var matchedKeyword, out var text);

            Assert.True(matched);
            Assert.Equal(keyword, matchedKeyword);
            Assert.Equal("something happens", text);
        }
    }
}
