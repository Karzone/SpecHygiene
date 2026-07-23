using SpecHygiene.Analysis;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// Proves the step-definition coverage path is dialect-aware (issue #1). A binding is counted USED
/// iff a matching step entered the corpus, so a German "# language: de" step matching a binding
/// demonstrates that localized keywords (Szenario:, Angenommen …) are now parsed. Before the fix the
/// German block boundaries were invisible, the step never entered the corpus, and the binding was
/// wrongly reported as unused/dead.
/// </summary>
public sealed class GherkinDialectCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdd-i18n-corpus-" + Guid.NewGuid().ToString("N"));
    private readonly string _features;
    private readonly string _stepsDir;

    public GherkinDialectCoverageTests()
    {
        _features = Path.Combine(_root, "Features");
        _stepsDir = Path.Combine(_root, "Steps");
        Directory.CreateDirectory(_features);
        Directory.CreateDirectory(_stepsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private void Feature(string name, string content) =>
        File.WriteAllText(Path.Combine(_features, name + ".feature"), content.ReplaceLineEndings("\n"));

    private void Binding(string pattern) =>
        File.WriteAllText(Path.Combine(_stepsDir, "Steps.cs"), $$"""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"{{pattern}}")]
                public void TheStep() { }
            }
            """.ReplaceLineEndings("\n"));

    private StepDefinitionCoverageReport Run() =>
        new StepDefinitionCoverageAnalyzer().AnalyzeCoverage(new[] { _stepsDir }, new[] { _features });

    [Fact]
    public void GermanStep_MatchesBinding_SoBindingIsUsed()
    {
        Binding("etwas passiert");
        Feature("de", """
            # language: de
            Funktionalität: F

            Szenario: s
                Angenommen etwas passiert
            """);

        var report = Run();

        // The German "Angenommen" step entered the corpus and matched the binding -> not dead.
        Assert.Equal(0, report.UnusedStepDefinitions);
    }

    [Fact]
    public void GermanNarrativePreamble_IsNotParsedAsStep()
    {
        // The prose line under Funktionalität starts with "Angenommen" but precedes any block header,
        // so it is narrative, not a step. A binding matching it must stay UNUSED.
        Binding("dieser Satz ist nur Prosa");
        Feature("de", """
            # language: de
            Funktionalität: F
            Angenommen dieser Satz ist nur Prosa

            Szenario: s
                Wenn etwas anderes passiert
            """);

        var report = Run();

        Assert.Equal(1, report.UnusedStepDefinitions);
    }
}
