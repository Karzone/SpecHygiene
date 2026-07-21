using System.Reflection;
using SpecHygiene.Analyzers;
using SpecHygiene.Models;
using Xunit;
using MatchType = SpecHygiene.Models.MatchType;

namespace SpecHygiene.Tests;

/// <summary>
/// Proof for N3 (containment-before-Exact classification fix) in
/// DuplicateAnalyzer.AnalyzeDuplicateGroup. The method is private, so we drive the REAL
/// production method via reflection rather than reimplementing its logic.
///
/// Pre-fix bug: OverlapPercentage = exactMatchCount / min(stepCounts), and the Exact branch
/// ran before the Superset/Subset branches — so any full containment scored 100% and was
/// labelled Exact, leaving Superset/Subset as unreachable dead code.
/// </summary>
public class ScenarioDuplicateClassificationTests
{
    private static ScenarioDuplicateType Classify(ScenarioMatch match)
    {
        var analyzer = new DuplicateAnalyzer(new AnalyzerSettings());

        var method = typeof(DuplicateAnalyzer).GetMethod(
            "AnalyzeDuplicateGroup",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        // AnalyzeDuplicateGroup(ScenarioInfo baseScenario, List<ScenarioMatch> matches).
        // baseScenario is unused by the method body; pass a placeholder.
        var placeholderBase = MakeScenario("base", 0);

        var result = method.Invoke(analyzer, new object[] { placeholderBase, new List<ScenarioMatch> { match } })!;

        // Returns (ScenarioDuplicateType Type, bool, List<string>) — Item1 is the type.
        return (ScenarioDuplicateType)result.GetType().GetField("Item1")!.GetValue(result)!;
    }

    private static ScenarioInfo MakeScenario(string name, int stepCount)
    {
        var fps = Enumerable.Range(0, stepCount).Select(i => $"{name}-fp{i}").ToList();
        var texts = Enumerable.Range(0, stepCount).Select(i => $"{name} step {i}").ToList();
        return new ScenarioInfo("Proj", "f.feature", @"C:\f.feature", name, 1, fps, texts, $"{name}-sfp");
    }

    /// <summary>Builds a ScenarioMatch with the fields AnalyzeDuplicateGroup actually reads.</summary>
    private static ScenarioMatch MakeMatch(
        int totalStepsBase,
        int totalStepsMatch,
        int matchingSteps,
        double overlapPercentage,
        MatchType matchType = MatchType.Exact)
    {
        return new ScenarioMatch(
            MakeScenario("match", totalStepsMatch),
            overlapPercentage,
            matchingSteps,
            totalStepsBase,
            totalStepsMatch,
            SameSequence: true,
            MatchingStepTexts: new List<string>(),
            UniqueToBase: new List<string>(),
            UniqueToMatch: new List<string>())
        {
            MatchType = matchType
        };
    }

    [Fact]
    public void EqualCount_FullMatch_StaysExact()
    {
        // 5 steps vs 5 steps, all matched, overlap 100 -> identical -> Exact.
        var type = Classify(MakeMatch(totalStepsBase: 5, totalStepsMatch: 5, matchingSteps: 5, overlapPercentage: 100));
        Assert.Equal(ScenarioDuplicateType.Exact, type);
    }

    [Fact]
    public void SmallerBaseFullyInsideLargerMatch_IsSuperset()
    {
        // base(3) fully contained in match(5): match contains all of base + more -> Superset.
        // Pre-fix this scored 100% overlap and was mislabelled Exact.
        var type = Classify(MakeMatch(totalStepsBase: 3, totalStepsMatch: 5, matchingSteps: 3, overlapPercentage: 100));
        Assert.Equal(ScenarioDuplicateType.Superset, type);
    }

    [Fact]
    public void SmallerMatchFullyInsideLargerBase_IsSubset()
    {
        // match(3) fully contained in base(5): base contains all of match + more -> Subset.
        var type = Classify(MakeMatch(totalStepsBase: 5, totalStepsMatch: 3, matchingSteps: 3, overlapPercentage: 100));
        Assert.Equal(ScenarioDuplicateType.Subset, type);
    }

    [Fact]
    public void PartialOverlap_StaysHighOverlap()
    {
        // 4 of 5 steps match, overlap 80, no fuzzy -> not full, not containment -> HighOverlap (unchanged).
        var type = Classify(MakeMatch(totalStepsBase: 5, totalStepsMatch: 5, matchingSteps: 4, overlapPercentage: 80));
        Assert.Equal(ScenarioDuplicateType.HighOverlap, type);
    }

    [Fact]
    public void ParameterVariation_Unchanged_NotStolenByContainment()
    {
        // Guardrail: a parameter-variation pair (matches via parameter-agnostic, not exact)
        // must still be ParameterVariation after moving containment ahead of it.
        var type = Classify(MakeMatch(
            totalStepsBase: 5, totalStepsMatch: 5, matchingSteps: 3, overlapPercentage: 60,
            matchType: MatchType.ParameterVariation));
        Assert.Equal(ScenarioDuplicateType.ParameterVariation, type);
    }
}
