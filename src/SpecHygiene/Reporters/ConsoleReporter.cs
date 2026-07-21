using SpecHygiene.Models;

namespace SpecHygiene.Reporters;

/// <summary>
/// Minimal text summary of a run. Intentionally tiny — the rich HTML report is the primary output;
/// this is the always-available fallback and the smoke check that the pipeline ran end to end.
/// </summary>
public sealed class ConsoleReporter : IReporter
{
    public string OutputFileName => "summary.txt";

    public Task GenerateAsync(DuplicateAnalysisReport report, string outputDirectory)
    {
        var lines = new List<string>
        {
            "SpecHygiene — analysis summary",
            "==============================",
            $"Feature files scanned : {report.FeatureFilesScanned}",
            $"Scenarios analysed    : {report.TotalScenariosAnalyzed}",
            $"Steps analysed        : {report.TotalStepsAnalyzed}",
            "",
            $"Data errors           : {report.DataErrors.Count}",
            $"Scenario duplicates   : {report.ScenarioDuplicates.Count}",
            $"Cross-project dups     : {report.CrossProjectDuplicates.Count}",
            $"Within-project dups    : {report.WithinProjectDuplicates.Count}",
        };

        if (report.StepDefinitionCoverage is { } cov)
        {
            lines.Add("");
            lines.Add($"Step coverage         : {cov.CoveragePercentage:F1}%");
            lines.Add($"Unused step defs      : {cov.UnusedDefinitions.Count}");
        }

        if (report.UnusedCodeReport is { } dead)
        {
            lines.Add("");
            lines.Add($"Unused methods        : {dead.UnusedMethods.Count}");
            lines.Add($"Unused classes        : {dead.UnusedClasses.Count}");
            lines.Add($"Unused interfaces     : {dead.UnusedInterfaces.Count}");
        }

        var text = string.Join(Environment.NewLine, lines);
        Console.WriteLine();
        Console.WriteLine(text);

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, OutputFileName), text);
        return Task.CompletedTask;
    }
}
