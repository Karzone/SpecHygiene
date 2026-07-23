using SpecHygiene.Analyzers;
using SpecHygiene.Models;
using SpecHygiene.Services;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// End-to-end proof for the localized-feature-file fix (issue #1). Before the fix, structural
/// keywords were hardcoded English, so a "# language: de" file was silently under-parsed: its
/// scenarios were never counted and its Examples table was never found (breaking data-error
/// detection). These tests drive the REAL public parse path (AnalyzeAsync, parse-only mode) over
/// temp files.
///
/// The English case is the regression guard: no existing test exercises the DuplicateAnalyzer
/// parse path, so it is asserted here alongside the German fix.
/// </summary>
public class GherkinDialectParsingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "spechygiene-i18n-" + Guid.NewGuid().ToString("N"));

    public GherkinDialectParsingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "SampleProject"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private void WriteFeature(string name, string content) =>
        File.WriteAllText(Path.Combine(_root, "SampleProject", name), content.ReplaceLineEndings("\n"));

    private async Task<DuplicateAnalysisReport> AnalyzeAsync()
    {
        var settings = new AnalyzerSettings();
        settings.Analysis.SolutionPaths = new List<string> { _root };
        var analyzer = new DuplicateAnalyzer(settings);
        // Parse-only: we only care about scenarios + data errors, not cross-scenario matching.
        return await analyzer.AnalyzeAsync(skipDuplicateMatching: true);
    }

    private const string GermanFeature = """
        # language: de
        Funktionalität: Kasse

          Szenario: Einfacher Kauf
            Angenommen der Warenkorb hat 2 Artikel
            Wenn ich zur Kasse gehe
            Dann sehe ich die Bestellübersicht

          Szenariogrundriss: Kauf mit Menge
            Angenommen der Warenkorb hat <menge> Artikel
            Wenn ich <fehlt> eingebe
            Dann sehe ich die Bestellübersicht
            Beispiele:
              | menge |
              | 2     |
        """;

    private const string EnglishFeature = """
        Feature: Checkout

          Scenario Outline: Buy with quantity
            Given the cart has <count> items
            When I enter <missing>
            Then I see the summary
            Examples:
              | count |
              | 2     |
        """;

    [Fact]
    public async Task GermanFile_ScenariosAreCounted()
    {
        WriteFeature("Kasse.feature", GermanFeature);

        var report = await AnalyzeAsync();

        // Both the plain Szenario and the Szenariogrundriss must be parsed (previously: zero).
        Assert.Equal(2, report.AllScenarios.Count);
        Assert.Contains(report.AllScenarios, s => s.ScenarioName == "Einfacher Kauf");
        Assert.Contains(report.AllScenarios, s => s.ScenarioName == "Kauf mit Menge");
    }

    [Fact]
    public async Task GermanFile_UndefinedPlaceholderInExamplesOutline_IsDetected()
    {
        WriteFeature("Kasse.feature", GermanFeature);

        var report = await AnalyzeAsync();

        // <fehlt> is used in a step but is not a column in Beispiele (only <menge> is).
        var error = Assert.Single(report.DataErrors, e => e.ErrorType == DataErrorType.UndefinedPlaceholder);
        Assert.Equal("Kauf mit Menge", error.Scenario);
        Assert.Contains("fehlt", error.UndefinedPlaceholders);
        Assert.DoesNotContain("menge", error.UndefinedPlaceholders); // defined -> not flagged
    }

    [Fact]
    public async Task EnglishFile_UndefinedPlaceholder_StillDetected_NoRegression()
    {
        WriteFeature("Checkout.feature", EnglishFeature);

        var report = await AnalyzeAsync();

        Assert.Single(report.AllScenarios);
        var error = Assert.Single(report.DataErrors, e => e.ErrorType == DataErrorType.UndefinedPlaceholder);
        Assert.Equal("Buy with quantity", error.Scenario);
        Assert.Contains("missing", error.UndefinedPlaceholders);
        Assert.DoesNotContain("count", error.UndefinedPlaceholders);
    }

    [Fact]
    public void Provider_LoadsEmbeddedResource_AndDetectsHeader()
    {
        // Guards the EmbeddedResource wiring: a wrong path would throw here, not at build time.
        var provider = new GherkinDialectProvider();

        var german = provider.Detect(new[] { "# language: de", "Funktionalität: X" });
        Assert.Equal("de", german.Language);
        Assert.Contains("Szenario", german.Scenario);

        var defaulted = provider.Detect(new[] { "Feature: X" });
        Assert.Equal("en", defaulted.Language);
    }
}
