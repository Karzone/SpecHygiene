using SpecHygiene.Analyzers;
using SpecHygiene.Analysis;
using SpecHygiene.Models;
using SpecHygiene.Reporters;
using Microsoft.Extensions.Configuration;

namespace SpecHygiene;

/// <summary>
/// SpecHygiene CLI. Runs one or more static hygiene checks over a Reqnroll/SpecFlow solution:
/// unused C# code (Roslyn), unused step definitions, feature-file data errors, and scenario
/// duplicates. No network, no AI — pure static analysis.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var settings = LoadSettings();

        // First non-flag arg is the solution/scan root.
        var root = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (!string.IsNullOrWhiteSpace(root))
        {
            var full = Path.GetFullPath(root);
            settings.Analysis.SolutionPaths = new List<string> { full };
            settings.UnusedCodeAnalysis.ScanPaths = new List<string> { full };
        }

        if (settings.Analysis.SolutionPaths.Count == 0)
        {
            Console.Error.WriteLine("No path given. Pass a solution/folder path, or set Analysis.SolutionPaths in appsettings.json.");
            PrintUsage();
            return 1;
        }

        // Flags select checks; --all (or no check flag) runs everything.
        bool any = args.Any(a => a is "--data-errors" or "--unused-steps" or "--unused-code");
        bool all = args.Contains("--all") || !any;

        settings.CoverageAnalysis.Enabled = all || args.Contains("--unused-steps");
        settings.UnusedCodeAnalysis.Enabled = all || args.Contains("--unused-code");
        bool wantDataErrors = all || args.Contains("--data-errors");
        settings.Analysis.ShowDataErrors = wantDataErrors;

        var outDir = ArgValue(args, "--out") ?? settings.Output.OutputDirectory;

        Console.WriteLine($"SpecHygiene — scanning {string.Join(", ", settings.Analysis.SolutionPaths)}");

        // --- Feature-file parse pass (cheap): yields data errors + the scan stats. No duplicate matching. ---
        var analyzer = new DuplicateAnalyzer(settings);
        var report = await analyzer.AnalyzeAsync(skipDuplicateMatching: true);

        if (!wantDataErrors)
            report.DataErrors.Clear();

        // --- Unused step definitions (independent of the parse pass) ---
        if (settings.CoverageAnalysis.Enabled)
        {
            var coverage = new StepDefinitionCoverageAnalyzer(
                settings.CoverageAnalysis.IgnoreTags,
                settings.CoverageAnalysis.IncludeIgnoredScenarios,
                useSyntacticDiscovery: settings.CoverageAnalysis.UseSyntacticDiscovery,
                knownIssuesCsvPath: settings.CoverageAnalysis.KnownIssuesCsvPath);

            var (stepDirs, featureDirs) = ResolveCoverageDirectories(settings);
            report.StepDefinitionCoverage = coverage.AnalyzeCoverage(stepDirs, featureDirs);
        }

        // --- Unused C# code (Roslyn) ---
        if (settings.UnusedCodeAnalysis.Enabled)
        {
            var scanPaths = settings.UnusedCodeAnalysis.ScanPaths.Count > 0
                ? settings.UnusedCodeAnalysis.ScanPaths
                : settings.Analysis.SolutionPaths;
            report.UnusedCodeReport = new UnusedCodeAnalyzer(settings.UnusedCodeAnalysis).Analyze(scanPaths);
        }

        // --- Report ---
        await new ConsoleReporter().GenerateAsync(report, outDir);
        if (settings.Output.GenerateHtml)
            await new HtmlReporter().GenerateAsync(report, outDir);
        Console.WriteLine($"\nReport written to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static (List<string> StepDirs, List<string> FeatureDirs) ResolveCoverageDirectories(AnalyzerSettings settings)
    {
        if (settings.CoverageAnalysis.UseStepAssemblyReferences)
        {
            var resolver = new StepAssemblyResolver();
            resolver.DiscoverAssemblies(settings.Analysis.SolutionPaths);

            var projectDirs = new List<string>();
            if (settings.Analysis.ProjectFolders is { Count: > 0 } folders)
            {
                foreach (var sln in settings.Analysis.SolutionPaths)
                    foreach (var proj in folders)
                    {
                        var p = Path.Combine(sln, proj);
                        if (Directory.Exists(p)) projectDirs.Add(p);
                    }
            }
            else
            {
                projectDirs.AddRange(settings.Analysis.SolutionPaths);
            }

            var stepDirs = resolver.GetAllStepDefinitionPaths(projectDirs, settings.Analysis.SolutionPaths);
            var featureDirs = resolver.GetAllTestProjectPaths();
            if (featureDirs.Count == 0) featureDirs = stepDirs.ToList();
            return (stepDirs, featureDirs);
        }

        var sd = settings.CoverageAnalysis.StepDefinitionPaths.Count > 0
            ? settings.CoverageAnalysis.StepDefinitionPaths
            : settings.Analysis.SolutionPaths.ToList();
        return (sd, settings.Analysis.SolutionPaths.ToList());
    }

    private static AnalyzerSettings LoadSettings()
    {
        var settings = new AnalyzerSettings();
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            new ConfigurationBuilder()
                .AddJsonFile(path, optional: true)
                .Build()
                .Bind(settings);
        }
        return settings;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            SpecHygiene — static hygiene checks for Reqnroll / SpecFlow solutions.

            Usage:
              spechygiene <path> [checks] [--out <dir>]

            Checks (default: all):
              --unused-code     Roslyn dead-code (unused methods/classes/interfaces)
              --unused-steps    Step definitions no scenario uses
              --data-errors     Feature-file data errors (undefined placeholders, etc.)
              --all             Run every check (default when none specified)

            Options:
              --out <dir>       Output directory (default ./reports)
              -h, --help        Show this help
            """);
    }
}
