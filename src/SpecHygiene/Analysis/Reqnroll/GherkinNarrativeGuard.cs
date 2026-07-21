// Pure, unit-testable "is this line inside a real Gherkin block" check, extracted from
// Program.cs's per-line step-parsing loop for the same reason PatternCompiler.cs /
// MatchingLogic.cs were: the decision needed a fast, isolated
// regression test against a real production example, not just "run the whole tool and eyeball
// the CSV".
//
// Confirmed production regression (2026-07-09): Program.cs's stepLineRegex
// (^\s*(Given|When|Then|And|But)\s+(.*?)\s*$) was applied to every non-blank, non-comment line
// in a .feature file with no awareness of Gherkin structure. A Feature's free-form narrative
// description block - ordinary English prose that appears directly under "Feature: ..." and
// before the first Background:/Scenario:/Scenario Outline: header - can legally start a
// continuation line with "and"/"but" as plain English (e.g.
// Acme.Api.Orders's PollSearchAndRetrieve.feature line 33: "and compares against
// outbound payload", continuing line 32's sentence; Acme.Api.E2E's "Add not allowed
// Additional Invoices in Estimate Only.feature" line 4: "and check for the appropriate
// error.", continuing line 3). Both were wrongly parsed as real Given step lines and reported
// as "undefined step" - pure narrative text that Reqnroll never evaluates as a step at all.
using System.Text.RegularExpressions;

namespace SpecHygiene.Analysis.Reqnroll
{
    public static class GherkinNarrativeGuard
    {
        private static readonly Regex BlockBoundaryRegex = new Regex(
            @"^\s*(Background|Scenario Outline|Scenario|Examples)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Confirmed empirically (2026-07-09, throwaway probe project referencing the real
        // `Gherkin` NuGet package Reqnroll itself depends on): step keywords are matched
        // case-sensitively - "and"/"when"/etc. in lowercase is never a step keyword to the real
        // parser. Deliberately NOT RegexOptions.IgnoreCase, unlike BlockBoundaryRegex above.
        private static readonly Regex StepLineRegex = new Regex(
            @"^\s*(Given|When|Then|And|But)\s+(.*?)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Matches a line against the real, case-sensitive Gherkin step-keyword grammar.
        /// </summary>
        public static bool TryMatchStepLine(string line, out string rawKeyword, out string text)
        {
            var m = StepLineRegex.Match(line);
            if (!m.Success)
            {
                rawKeyword = "";
                text = "";
                return false;
            }
            rawKeyword = m.Groups[1].Value;
            text = m.Groups[2].Value;
            return true;
        }

        /// <summary>
        /// For each line index, returns whether that line occurs at or after the first
        /// Background:/Scenario:/Scenario Outline:/Examples: header in the file. Lines before
        /// the first such header - i.e. the Feature's free-text narrative preamble - are never
        /// real step lines, even if they happen to start with "and"/"but".
        /// </summary>
        public static bool[] ComputeInsideBlockFlags(string[] lines)
        {
            var flags = new bool[lines.Length];
            bool insideBlock = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.Length > 0 && BlockBoundaryRegex.IsMatch(trimmed))
                {
                    insideBlock = true;
                }
                flags[i] = insideBlock;
            }
            return flags;
        }
    }
}
