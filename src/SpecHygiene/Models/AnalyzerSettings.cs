namespace SpecHygiene.Models;

public class AnalyzerSettings
{
    public AnalysisSettings Analysis { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public ThresholdSettings Thresholds { get; set; } = new();
    public CoverageAnalysisSettings CoverageAnalysis { get; set; } = new();
    public UnusedCodeAnalysisSettings UnusedCodeAnalysis { get; set; } = new();
}

public class AnalysisSettings
{
    public List<string> SolutionPaths { get; set; } = new();
    public List<string>? ProjectFolders { get; set; }

    public string FeatureFilePattern { get; set; } = "*.feature";
    public List<string> ExcludeFolders { get; set; } = new() { "bin", "obj", "node_modules", ".git" };
    public List<string> StepKeywords { get; set; } = new() { "Given", "When", "Then", "And", "But" };
    public List<string> ExcludeKeywordsFromDuplicates { get; set; } = new() { "Given" };

    /// <summary>
    /// Skip the main duplicate analysis (useful for running only other features, e.g. unused code).
    /// </summary>
    public bool SkipDuplicateAnalysis { get; set; } = false;

    /// <summary>
    /// When false, data-error entries are dropped before reporting. The errors are still detected
    /// during scenario parsing (cheap); they just don't surface.
    /// </summary>
    public bool ShowDataErrors { get; set; } = true;

    // Duplicate mode. Scenario is the meaningful hygiene signal — two whole scenarios that are
    // identical / near-identical (a copy-pasted test). Step mode only counts how often a step is
    // reused, which in BDD is normal and intended, not a problem — so it is not the default.
    public DuplicateMode DuplicateMode { get; set; } = DuplicateMode.Scenario;
    public int ScenarioOverlapThreshold { get; set; } = 80;
    public int MinStepsForScenarioComparison { get; set; } = 3;
    public int MaxUniqueStepsForDuplicate { get; set; } = 5;

    // Enhanced matching options
    public bool EnableFuzzyMatching { get; set; } = true;
    public double FuzzyMatchThreshold { get; set; } = 85;
    public bool EnableKeywordAgnosticMatching { get; set; } = true;
    public bool EnableParameterAgnosticMatching { get; set; } = true;
    public bool IncludeBackgroundSteps { get; set; } = true;
    public bool EnableCrossFeatureComparison { get; set; } = true;

    // Tag filtering
    public List<string> ExcludeScenarioTags { get; set; } = new() { "@ignore", "@wip", "@skip", "@manual" };
    public List<string> IncludeOnlyTags { get; set; } = new(); // Empty means include all

    // Performance optimization settings
    public int MaxComparisonsPerProject { get; set; } = 500000;
    public bool EnableParallelProcessing { get; set; } = true;
    public int MaxDegreeOfParallelism { get; set; } = 4;
    public bool EnableBucketOptimization { get; set; } = true;

    /// <summary>
    /// Settings for resolving Reqnroll @DataSource:foo.csv tags so that CSV header columns count as
    /// defined placeholders for Scenario Outlines.
    /// </summary>
    public DataSourceSettings DataSource { get; set; } = new();
}

public class DataSourceSettings
{
    /// <summary>Reserved. Today resolution is always: absolute → relative-to-feature → AdditionalSearchPaths.</summary>
    public string PathResolution { get; set; } = "RelativeToFeature";

    /// <summary>Extra folders to search if the CSV is not next to the feature file.</summary>
    public string[] AdditionalSearchPaths { get; set; } = Array.Empty<string>();
}

public enum DuplicateMode
{
    Step,
    Scenario
}

public class OutputSettings
{
    public string OutputDirectory { get; set; } = "./reports";
    public bool GenerateHtml { get; set; } = true;
    public bool GenerateMarkdown { get; set; } = false;
    public bool GenerateJson { get; set; } = false;
}

public class ThresholdSettings
{
    public int MaxCrossProjectDuplicates { get; set; } = 100;
    public int MaxWithinProjectDuplicates { get; set; } = 200;
    public int MaxDataErrors { get; set; } = 500;
    public bool FailOnThresholdExceeded { get; set; } = false;
}

/// <summary>
/// Settings for step definition coverage analysis (finds unused / orphaned Reqnroll step bindings).
/// </summary>
public class CoverageAnalysisSettings
{
    /// <summary>Enable or disable step definition coverage analysis.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Optional path to a known-issues CSV (SourceFile,MethodName,Comment) listing dead bindings that
    /// have already been triaged and accepted. They stay counted as unused — the CSV does not dispute
    /// the verdict — but are listed separately so each run's actionable list is what is NEW. Absent or
    /// unreadable means no rows, never a failed run.
    /// </summary>
    public string? KnownIssuesCsvPath { get; set; }

    /// <summary>
    /// Find step bindings by parsing C# syntax (Roslyn) instead of by regex. Default true. The
    /// syntactic pass gates on [Binding] (honouring inheritance), sees bare [Given] bindings the regex
    /// could not express, and resolves enum / transform parameter types from source. Set FALSE to fall
    /// back to the legacy regex parser.
    /// </summary>
    public bool UseSyntacticDiscovery { get; set; } = true;

    /// <summary>Directories containing step definition C# files. If empty, uses Analysis.SolutionPaths.</summary>
    public List<string> StepDefinitionPaths { get; set; } = new();

    /// <summary>File patterns to search for step definitions.</summary>
    public List<string> StepDefinitionFilePatterns { get; set; } = new()
    {
        "*Steps.cs",
        "*StepDefinitions.cs",
        "*Bindings.cs"
    };

    /// <summary>Tags that indicate a scenario is ignored/skipped.</summary>
    public List<string> IgnoreTags { get; set; } = new()
    {
        "@ignore", "@skip", "@wip", "@pending", "@manual", "@disabled"
    };

    /// <summary>Include steps from ignored scenarios in the analysis.</summary>
    public bool IncludeIgnoredScenarios { get; set; } = false;

    /// <summary>Minimum similarity (0-1) to suggest a closest match for orphaned steps.</summary>
    public double MinSimilarityForSuggestion { get; set; } = 0.5;

    /// <summary>Maximum number of orphaned steps to report (0 = unlimited).</summary>
    public int MaxOrphanedStepsToReport { get; set; } = 100;

    /// <summary>Show single-use step definitions in the report.</summary>
    public bool ReportSingleUseDefinitions { get; set; } = true;

    /// <summary>Number of most-used step definitions to show.</summary>
    public int TopUsedDefinitionsCount { get; set; } = 20;

    /// <summary>
    /// Use reqnroll.json/specflow.json to determine which step assemblies to scan. When enabled, only
    /// scans assemblies referenced by each project's config file — big speedup for large solutions.
    /// </summary>
    public bool UseStepAssemblyReferences { get; set; } = true;
}

/// <summary>
/// Settings for unused code analysis (Roslyn semantic dead-code detection).
/// </summary>
public class UnusedCodeAnalysisSettings
{
    /// <summary>Enable or disable unused code analysis.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Root directories to scan. Can be broader than the main SolutionPaths (e.g. a parent directory
    /// containing API, UI, Shared projects). If empty, falls back to Analysis.SolutionPaths.
    /// </summary>
    public List<string> ScanPaths { get; set; } = new();

    /// <summary>Analyze methods for unused references.</summary>
    public bool AnalyzeMethods { get; set; } = true;

    /// <summary>Analyze classes for unused references.</summary>
    public bool AnalyzeClasses { get; set; } = true;

    /// <summary>Analyze interfaces for unused references.</summary>
    public bool AnalyzeInterfaces { get; set; } = true;

    /// <summary>Include private methods (more likely to be dead code).</summary>
    public bool IncludePrivateMethods { get; set; } = true;

    /// <summary>Include internal methods.</summary>
    public bool IncludeInternalMethods { get; set; } = true;

    /// <summary>
    /// Include public and protected members. OFF by default: in a solution with tests / DI /
    /// controllers / serialization / reflection, public members are frequently used through call paths
    /// no static analyzer can see, so flagging them is noisy. Private + internal members are
    /// assembly-scoped — that's where dead-code detection is sound. Turn on for a stricter sweep.
    /// </summary>
    public bool IncludePublicMembers { get; set; } = false;

    /// <summary>Patterns to exclude from analysis (class/method names). Supports * wildcards.</summary>
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "*Tests", "*Test", "Test*", "*Fixture",
        "Program", "Startup", "Main",
        "ConfigureServices", "Configure", "Dispose",
        "ToString", "GetHashCode", "Equals"
    };

    /// <summary>Attributes that exclude a method from analysis (test methods, hooks, etc.).</summary>
    public List<string> ExcludeAttributes { get; set; } = new()
    {
        // SpecFlow/Reqnroll step definitions
        "Given", "When", "Then", "And", "But", "StepDefinition",
        // SpecFlow/Reqnroll hooks
        "BeforeScenario", "AfterScenario", "BeforeFeature", "AfterFeature",
        "BeforeStep", "AfterStep", "BeforeTestRun", "AfterTestRun",
        "BeforeScenarioBlock", "AfterScenarioBlock", "Binding", "Scope",
        // xUnit
        "Fact", "Theory", "InlineData", "ClassData", "MemberData",
        // NUnit
        "Test", "TestCase", "TestCaseSource", "SetUp", "TearDown",
        "OneTimeSetUp", "OneTimeTearDown", "TestFixture",
        // MSTest
        "TestMethod", "TestClass", "TestInitialize", "TestCleanup",
        "ClassInitialize", "ClassCleanup", "AssemblyInitialize", "AssemblyCleanup",
        // ASP.NET
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch",
        "Route", "ApiController", "Controller",
        // Serialization
        "JsonConstructor", "JsonProperty", "DataMember", "DataContract",
        // DI
        "Inject", "Autowired"
    };

    /// <summary>File patterns to exclude (generated files, designer files, AssemblyInfo, etc.).</summary>
    public List<string> ExcludeFilePatterns { get; set; } = new()
    {
        "*.g.cs", "*.designer.cs", "*.generated.cs",
        "*.feature.cs", "AssemblyInfo.cs", "GlobalUsings.cs"
    };

    /// <summary>Directories to exclude from analysis.</summary>
    public List<string> ExcludeDirectories { get; set; } = new()
    {
        "obj", "bin", "node_modules", ".git", "packages", "TestResults"
    };
}
