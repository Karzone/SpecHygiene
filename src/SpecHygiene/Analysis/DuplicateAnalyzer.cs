using SpecHygiene.Services;
using SpecHygiene.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MatchType = SpecHygiene.Models.MatchType;

namespace SpecHygiene.Analyzers;

public class DuplicateAnalyzer
{
    private readonly AnalyzerSettings _settings;
    private readonly HashSet<string> _reviewedFingerprints;
    private readonly StepNormalizer _normalizer;
    private readonly DataSourceResolver _dataSourceResolver;
    private readonly GherkinDialectProvider _dialectProvider;

    private static readonly Regex PlaceholderRegex = new(@"<([^>]+)>", RegexOptions.Compiled);
    private static readonly Regex ValidPlaceholderNameRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    public DuplicateAnalyzer(
        AnalyzerSettings settings,
        HashSet<string>? reviewedFingerprints = null,
        DataSourceResolver? dataSourceResolver = null,
        GherkinDialectProvider? dialectProvider = null)
    {
        _settings = settings;
        _reviewedFingerprints = reviewedFingerprints ?? new HashSet<string>();
        _normalizer = new StepNormalizer();
        _dataSourceResolver = dataSourceResolver ?? new DataSourceResolver(settings.Analysis.DataSource);
        _dialectProvider = dialectProvider ?? GherkinDialectProvider.Default;
    }

    /// <summary>
    /// When skipDuplicateMatching is true, runs only the feature-file parse pass (cheap)
    /// and skips the expensive cross-scenario duplicate-matching phase. Redundant-step
    /// analysis still runs because it only needs the parsed step list. Used when the
    /// user wants Data Errors or Redundant Steps without paying for full duplicate
    /// matching.
    /// </summary>
    public async Task<DuplicateAnalysisReport> AnalyzeAsync(bool skipDuplicateMatching = false)
    {
        var allSteps = new List<StepOccurrence>();
        var allScenarios = new List<ScenarioInfo>();
        var allDataErrors = new List<DataError>();
        var allParseFailures = new List<ParseFailure>();
        var projectsScanned = new HashSet<string>();
        var featureFilesScanned = 0;
        var scenariosExcludedByTags = 0;

        foreach (var solutionPath in _settings.Analysis.SolutionPaths)
        {
            var normalizedPath = Path.GetFullPath(solutionPath.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(normalizedPath))
            {
                Console.WriteLine($"   Path not found: {normalizedPath}");
                continue;
            }

            Console.WriteLine($"Scanning: {normalizedPath}");

            var projectFolders = GetProjectFolders(normalizedPath);

            foreach (var projectFolder in projectFolders)
            {
                var projectName = Path.GetFileName(projectFolder);
                projectsScanned.Add(projectName);

                var featureFiles = Directory.GetFiles(projectFolder, _settings.Analysis.FeatureFilePattern, SearchOption.AllDirectories)
                    .Where(f => !_settings.Analysis.ExcludeFolders.Any(ex =>
                        f.Contains(Path.DirectorySeparatorChar + ex + Path.DirectorySeparatorChar)))
                    .ToList();

                Console.WriteLine($"   {projectName}: {featureFiles.Count} feature files");

                foreach (var featureFile in featureFiles)
                {
                    featureFilesScanned++;
                    var (steps, scenarios, errors, excludedCount, parseFailure) = await ParseFeatureFileEnhancedAsync(projectName, featureFile);
                    allSteps.AddRange(steps);
                    allScenarios.AddRange(scenarios);
                    allDataErrors.AddRange(errors);
                    scenariosExcludedByTags += excludedCount;
                    if (parseFailure != null)
                        allParseFailures.Add(parseFailure);
                }
            }
        }

        Console.WriteLine($"   Total steps: {allSteps.Count}, Scenarios: {allScenarios.Count}");
        Console.WriteLine($"   Scenarios excluded by tags: {scenariosExcludedByTags}");
        Console.WriteLine($"   Data errors: {allDataErrors.Count}");
        Console.WriteLine($"   Analysis mode: {_settings.Analysis.DuplicateMode}");

        var report = new DuplicateAnalysisReport
        {
            DataErrors = allDataErrors,
            AllScenarios = allScenarios,
            ParseFailures = allParseFailures,
            TotalStepsAnalyzed = allSteps.Count,
            TotalScenariosAnalyzed = allScenarios.Count,
            ScenariosExcludedByTags = scenariosExcludedByTags,
            FeatureFilesScanned = featureFilesScanned,
            ProjectsScanned = projectsScanned.Count,
            AnalysisMode = _settings.Analysis.DuplicateMode
        };
        if (allParseFailures.Count > 0)
            Console.WriteLine($"   WARNING: {allParseFailures.Count} feature file(s) failed to parse (recorded in inventory).");

        if (skipDuplicateMatching)
        {
            Console.WriteLine("   (Skipping expensive cross-scenario matching — parse-only mode.)");
        }
        else if (_settings.Analysis.DuplicateMode == DuplicateMode.Step)
        {
            var stepReport = GenerateStepReport(allSteps);
            report.CrossProjectDuplicates = stepReport.CrossProjectDuplicates;
            report.WithinProjectDuplicates = stepReport.WithinProjectDuplicates;
            report.SameStepDifferentData = stepReport.SameStepDifferentData;
        }
        else
        {
            var (duplicates, stats) = GenerateEnhancedScenarioReport(allScenarios);
            report.ScenarioDuplicates = duplicates;
            report.FuzzyMatchesFound = stats.FuzzyMatches;
            report.ParameterVariationsFound = stats.ParameterVariations;
            report.PotentialStepReuse = stats.PotentialStepReuse;
            report.ScenarioOutlineCandidates = stats.ScenarioOutlineCandidates;
            Console.WriteLine($"   Scenario duplicates found: {duplicates.Count}");
            Console.WriteLine($"   Fuzzy matches: {stats.FuzzyMatches}, Parameter variations: {stats.ParameterVariations}");
            Console.WriteLine($"   Potential step reuse: {stats.PotentialStepReuse}, Scenario Outline candidates: {stats.ScenarioOutlineCandidates}");
        }

        return report;
    }

    private async Task<(List<StepOccurrence> Steps, List<ScenarioInfo> Scenarios, List<DataError> Errors, int ExcludedCount, ParseFailure? ParseFailure)>
        ParseFeatureFileEnhancedAsync(string project, string filePath)
    {
        var occurrences = new List<StepOccurrence>();
        var scenarios = new List<ScenarioInfo>();
        var errors = new List<DataError>();
        var excludedCount = 0;
        ParseFailure? parseFailure = null;

        try
        {
            var lines = await File.ReadAllLinesAsync(filePath);

            // Resolve the file's Gherkin dialect from its "# language:" header (English if absent).
            // For English we keep honoring the user-configured StepKeywords (unchanged behavior);
            // for a localized file we use that dialect's own step keywords.
            var dialect = _dialectProvider.Detect(lines);
            var stepKeywords = dialect.Language.Equals(GherkinDialectProvider.DefaultLanguage, StringComparison.OrdinalIgnoreCase)
                ? _settings.Analysis.StepKeywords
                : dialect.StepKeywords;

            var currentScenario = "";
            var currentScenarioLine = 0;
            var isScenarioOutline = false;
            var examplesPlaceholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasExamplesSection = false;
            var scenarioTypeErrorAdded = false;
            var dataSourceFailed = false;
            var currentTags = new List<string>();
            
            // Background tracking
            var backgroundSteps = new List<string>();
            var backgroundFingerprints = new List<string>();
            var backgroundNormalized = new List<string>();
            var backgroundOriginalSteps = new List<string>();   // raw text incl. keyword (display only)
            var backgroundDataTables = new List<List<Dictionary<string, string>>>();   // lockstep with backgroundSteps
            var inBackground = false;

            // Current scenario tracking
            var currentScenarioStepFingerprints = new List<string>();
            var currentScenarioStepTexts = new List<string>();
            var currentScenarioNormalizedTexts = new List<string>();
            var currentScenarioOriginalStepTexts = new List<string>();   // raw text incl. keyword (display only)
            var currentScenarioStepDataTables = new List<List<Dictionary<string, string>>>();   // lockstep with StepTexts

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                // Track tags
                if (line.StartsWith("@"))
                {
                    var tags = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(t => t.StartsWith("@"))
                        .ToList();
                    currentTags.AddRange(tags);
                    continue;
                }

                // Track Background
                if (dialect.IsBackground(line))
                {
                    inBackground = true;
                    backgroundSteps.Clear();
                    backgroundFingerprints.Clear();
                    backgroundNormalized.Clear();
                    backgroundOriginalSteps.Clear();
                    backgroundDataTables.Clear();
                    continue;
                }

                // Track Scenario or Scenario Outline
                if (dialect.IsScenarioStart(line))
                {
                    inBackground = false;

                    // Save previous scenario if exists
                    if (!string.IsNullOrEmpty(currentScenario) && currentScenarioStepFingerprints.Any())
                    {
                        scenarios.Add(CreateEnhancedScenarioInfo(
                            project, filePath, currentScenario, currentScenarioLine,
                            currentScenarioStepFingerprints, currentScenarioStepTexts, currentScenarioNormalizedTexts,
                            currentTags, backgroundSteps, backgroundFingerprints, currentScenarioOriginalStepTexts, isScenarioOutline,
                            currentScenarioStepDataTables));
                    }

                    // Check if scenario should be excluded by tags
                    if (ShouldExcludeByTags(currentTags))
                    {
                        excludedCount++;
                        currentTags.Clear();
                        currentScenario = "";
                        continue;
                    }

                    isScenarioOutline = dialect.IsScenarioOutline(line);

                    currentScenario = line.Split(':', 2).LastOrDefault()?.Trim() ?? "";
                    currentScenarioLine = i + 1;
                    examplesPlaceholders.Clear();
                    scenarioTypeErrorAdded = false;
                    currentScenarioStepFingerprints = new List<string>();
                    currentScenarioStepTexts = new List<string>();
                    currentScenarioNormalizedTexts = new List<string>();
                    currentScenarioOriginalStepTexts = new List<string>();
                    currentScenarioStepDataTables = new List<List<Dictionary<string, string>>>();

                    // Include background steps if enabled. StepDataTables must be prepended in
                    // lockstep too, otherwise every scenario table renders offset by the background
                    // step count (a table would pair with the wrong step in the AI prompt).
                    if (_settings.Analysis.IncludeBackgroundSteps && backgroundSteps.Any())
                    {
                        currentScenarioStepFingerprints.AddRange(backgroundFingerprints);
                        currentScenarioStepTexts.AddRange(backgroundSteps);
                        currentScenarioNormalizedTexts.AddRange(backgroundNormalized);
                        currentScenarioOriginalStepTexts.AddRange(backgroundOriginalSteps);
                        currentScenarioStepDataTables.AddRange(backgroundDataTables);
                    }

                    // Get Examples info - use tuple deconstruction
                    var (placeholders, hasExamples) = FindExamplesInfo(lines, i, dialect);
                    examplesPlaceholders = placeholders;
                    hasExamplesSection = hasExamples;

                    // Merge in @DataSource:foo.csv header columns as defined placeholders.
                    // CSV-backed Scenario Outlines (Reqnroll external data) have placeholders
                    // resolved from CSV columns, not an inline Examples table.
                    dataSourceFailed = false;
                    var dataSourceValue = DataSourceResolver.ExtractDataSourceValue(currentTags);
                    if (!string.IsNullOrEmpty(dataSourceValue))
                    {
                        var dataSourceResult = _dataSourceResolver.Resolve(dataSourceValue, filePath);
                        if (dataSourceResult.Found)
                        {
                            foreach (var header in dataSourceResult.Headers)
                            {
                                examplesPlaceholders.Add(header);
                            }
                        }
                        else
                        {
                            // CSV unresolved: emit one root-cause error and suppress the
                            // per-step UndefinedPlaceholder cascade that would otherwise
                            // flag every CSV-resolved placeholder in this scenario.
                            dataSourceFailed = true;
                            errors.Add(new DataError(
                                project,
                                Path.GetFileName(filePath),
                                filePath,
                                currentScenario,
                                $"@DataSource:{dataSourceValue}",
                                currentScenarioLine,
                                new List<string>(),
                                new List<string> { dataSourceResult.Error ?? "CSV not found" },
                                DataErrorType.DataSourceNotFound,
                                hasExamplesSection
                            ));
                        }
                    }

                    // Add ScenarioWithExamples error ONLY for Scenario with Examples (wrong type)
                    // This is separate from UndefinedPlaceholder errors
                    if (!isScenarioOutline && hasExamplesSection && !scenarioTypeErrorAdded)
                    {
                        errors.Add(new DataError(
                            project,
                            Path.GetFileName(filePath),
                            filePath,
                            currentScenario,
                            $"Scenario: {currentScenario}",
                            currentScenarioLine,
                            new List<string>(),  // No undefined placeholders for this error type
                            examplesPlaceholders.ToList(),
                            DataErrorType.ScenarioWithExamples,
                            hasExamplesSection
                        ));
                        scenarioTypeErrorAdded = true;
                    }

                    currentTags = new List<string>(); // Reset tags for next scenario
                    continue;
                }

                // Skip Examples, Feature lines etc.
                if (dialect.IsExamples(line) ||
                    dialect.IsFeature(line) ||
                    line.StartsWith("|") ||
                    string.IsNullOrWhiteSpace(line) ||
                    line.StartsWith("#"))
                {
                    continue;
                }

                // Check if this is a step line
                if (IsStepLine(line, stepKeywords))
                {
                    var stepText = line;
                    var dataTable = new List<Dictionary<string, string>>();

                    var (table, tableLines, linesConsumed) = ParseDataTableWithLines(lines, i + 1);
                    dataTable = table;

                    var fingerprint = GenerateFingerprint(stepText, dataTable);
                    var normalizedText = _normalizer.FullNormalize(stepText);

                    if (inBackground)
                    {
                        backgroundSteps.Add(NormalizeStepText(stepText));
                        backgroundFingerprints.Add(fingerprint);
                        backgroundNormalized.Add(normalizedText);
                        backgroundOriginalSteps.Add(stepText);
                        backgroundDataTables.Add(dataTable);
                    }
                    else if (!string.IsNullOrEmpty(currentScenario))
                    {
                        // KEY FIX: Only check for undefined placeholders if:
                        // 1. It's a proper Scenario Outline (isScenarioOutline = true), OR
                        // 2. It's a regular Scenario WITHOUT an Examples section (hasExamplesSection = false)
                        // 
                        // DO NOT check if it's a Scenario WITH Examples (ScenarioWithExamples error)
                        // because the placeholders ARE defined in Examples, just the Scenario type is wrong
                        if ((isScenarioOutline || !hasExamplesSection) && !dataSourceFailed)
                        {
                            var stepErrors = CheckForUndefinedPlaceholders(
                                project, filePath, currentScenario, stepText, i + 1,
                                tableLines, examplesPlaceholders);
                            errors.AddRange(stepErrors);
                        }

                        currentScenarioStepFingerprints.Add(fingerprint);
                        currentScenarioStepTexts.Add(NormalizeStepText(stepText));
                        currentScenarioNormalizedTexts.Add(normalizedText);
                        currentScenarioOriginalStepTexts.Add(stepText);
                        currentScenarioStepDataTables.Add(dataTable);

                        var stepWithData = new StepWithData(NormalizeStepText(stepText), dataTable, fingerprint);

                        occurrences.Add(new StepOccurrence(
                            project,
                            Path.GetFileName(filePath),
                            filePath,
                            currentScenario,
                            stepWithData,
                            i + 1,
                            stepText
                        ));
                    }

                    i += linesConsumed;
                }
            }

            // Save last scenario
            if (!string.IsNullOrEmpty(currentScenario) && currentScenarioStepFingerprints.Any())
            {
                if (!ShouldExcludeByTags(currentTags))
                {
                    scenarios.Add(CreateEnhancedScenarioInfo(
                        project, filePath, currentScenario, currentScenarioLine,
                        currentScenarioStepFingerprints, currentScenarioStepTexts, currentScenarioNormalizedTexts,
                        currentTags, backgroundSteps, backgroundFingerprints, currentScenarioOriginalStepTexts, isScenarioOutline,
                        currentScenarioStepDataTables));
                }
                else
                {
                    excludedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      Error parsing {filePath}: {ex.Message}");
            parseFailure = new ParseFailure(project, filePath, ex.Message);
        }

        return (occurrences, scenarios, errors, excludedCount, parseFailure);
    }

    private bool ShouldExcludeByTags(List<string> tags)
    {
        if (_settings.Analysis.ExcludeScenarioTags.Any(excludeTag =>
            tags.Any(t => t.Equals(excludeTag, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (_settings.Analysis.IncludeOnlyTags.Any())
        {
            return !_settings.Analysis.IncludeOnlyTags.Any(includeTag =>
                tags.Any(t => t.Equals(includeTag, StringComparison.OrdinalIgnoreCase)));
        }

        return false;
    }

    private ScenarioInfo CreateEnhancedScenarioInfo(
        string project, string filePath, string scenarioName, int lineNumber,
        List<string> stepFingerprints, List<string> stepTexts, List<string> normalizedTexts,
        List<string> tags, List<string> backgroundSteps, List<string> backgroundFingerprints,
        List<string> originalStepTexts, bool isScenarioOutline,
        List<List<Dictionary<string, string>>> stepDataTables)
    {
        var sortedFingerprints = stepFingerprints.OrderBy(f => f).ToList();
        var combinedForHash = string.Join("||", sortedFingerprints);

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(combinedForHash));
        var scenarioFingerprint = Convert.ToBase64String(bytes)[..20];

        return new ScenarioInfo(
            project,
            Path.GetFileName(filePath),
            filePath,
            scenarioName,
            lineNumber,
            stepFingerprints.ToList(),
            stepTexts.ToList(),
            scenarioFingerprint
        )
        {
            NormalizedStepTexts = normalizedTexts.ToList(),
            Tags = tags.ToList(),
            BackgroundSteps = backgroundSteps.ToList(),
            BackgroundFingerprints = backgroundFingerprints.ToList(),
            OriginalStepTexts = originalStepTexts.ToList(),
            ScenarioType = isScenarioOutline ? "ScenarioOutline" : "Scenario",
            StepDataTables = stepDataTables.Select(t => t.ToList()).ToList()
        };
    }

    private (List<ScenarioDuplicateGroup> Duplicates, AnalysisStats Stats) GenerateEnhancedScenarioReport(List<ScenarioInfo> allScenarios)
    {
        var duplicateGroups = new ConcurrentBag<ScenarioDuplicateGroup>();
        var processedPairs = new ConcurrentDictionary<string, byte>();
        var threshold = _settings.Analysis.ScenarioOverlapThreshold;
        var minSteps = _settings.Analysis.MinStepsForScenarioComparison;
        var maxUniqueSteps = _settings.Analysis.MaxUniqueStepsForDuplicate;

        var stats = new AnalysisStats();
        var fuzzyMatchCount = 0;
        var parameterVariationCount = 0;
        var potentialStepReuseCount = 0;
        var outlineCandidateCount = 0;

        var filteredScenarios = allScenarios
            .Where(s => s.StepFingerprints.Count >= minSteps)
            .ToList();

        Console.WriteLine($"   Scenarios after filtering (min {minSteps} steps): {filteredScenarios.Count}");

        var scenariosByProject = filteredScenarios
            .GroupBy(s => s.Project)
            .ToList();

        var totalComparisons = 0;
        var projectCount = 0;

        foreach (var projectGroup in scenariosByProject)
        {
            projectCount++;
            var projectScenarios = projectGroup.ToList();
            var projectName = projectGroup.Key;

            Console.WriteLine($"   [{projectCount}/{scenariosByProject.Count}] Processing {projectName}: {projectScenarios.Count} scenarios");

            if (projectScenarios.Count < 2)
            {
                Console.WriteLine($"      Skipped (less than 2 scenarios)");
                continue;
            }

            var maxComparisonsPerProject = _settings.Analysis.MaxComparisonsPerProject;
            var estimatedComparisons = (long)projectScenarios.Count * (projectScenarios.Count - 1) / 2;

            List<ScenarioInfo> scenariosToProcess;
            bool useBucketOptimization = _settings.Analysis.EnableBucketOptimization && estimatedComparisons > maxComparisonsPerProject;

            if (useBucketOptimization)
            {
                Console.WriteLine($"      Using bucket optimization for {estimatedComparisons:N0} potential comparisons");
                scenariosToProcess = projectScenarios;
            }
            else if (estimatedComparisons > maxComparisonsPerProject && !_settings.Analysis.EnableBucketOptimization)
            {
                Console.WriteLine($"      Warning: {estimatedComparisons:N0} comparisons needed, limiting to first {Math.Sqrt(maxComparisonsPerProject * 2):F0} scenarios");
                var limitCount = (int)Math.Sqrt(maxComparisonsPerProject * 2);
                scenariosToProcess = projectScenarios.Take(limitCount).ToList();
            }
            else
            {
                scenariosToProcess = projectScenarios;
            }

            var projectDuplicates = 0;
            var comparisonCount = 0;

            List<List<ScenarioInfo>> comparisonGroups;

            if (_settings.Analysis.EnableCrossFeatureComparison)
            {
                comparisonGroups = new List<List<ScenarioInfo>> { scenariosToProcess };
            }
            else
            {
                comparisonGroups = scenariosToProcess
                    .GroupBy(s => s.FeatureFile)
                    .Select(g => g.ToList())
                    .Where(g => g.Count >= 2)
                    .ToList();
            }

            foreach (var scenarios in comparisonGroups)
            {
                int localComparisonCount;
                int localDuplicates;
                int localFuzzyMatches;
                int localParameterVariations;
                int localStepReuse;
                int localOutlineCandidates;

                if (_settings.Analysis.EnableParallelProcessing && scenarios.Count > 50)
                {
                    // Use parallel processing with bucket optimization for large scenario sets
                    (localComparisonCount, localDuplicates, localFuzzyMatches, localParameterVariations, localStepReuse, localOutlineCandidates) =
                        ProcessScenariosParallel(scenarios, duplicateGroups, processedPairs, threshold, maxUniqueSteps, useBucketOptimization);
                }
                else
                {
                    // Use sequential processing for smaller sets
                    (localComparisonCount, localDuplicates, localFuzzyMatches, localParameterVariations, localStepReuse, localOutlineCandidates) =
                        ProcessScenariosSequential(scenarios, duplicateGroups, processedPairs, threshold, maxUniqueSteps);
                }

                comparisonCount += localComparisonCount;
                projectDuplicates += localDuplicates;
                Interlocked.Add(ref fuzzyMatchCount, localFuzzyMatches);
                Interlocked.Add(ref parameterVariationCount, localParameterVariations);
                Interlocked.Add(ref potentialStepReuseCount, localStepReuse);
                Interlocked.Add(ref outlineCandidateCount, localOutlineCandidates);
            }

            totalComparisons += comparisonCount;
            Console.WriteLine($"      Comparisons: {comparisonCount:N0}, Duplicates found: {projectDuplicates}");
        }

        Console.WriteLine($"   Total comparisons: {totalComparisons:N0}");
        Console.WriteLine($"   Total duplicate groups: {duplicateGroups.Count}");

        stats.FuzzyMatches = fuzzyMatchCount;
        stats.ParameterVariations = parameterVariationCount;
        stats.PotentialStepReuse = potentialStepReuseCount;
        stats.ScenarioOutlineCandidates = outlineCandidateCount;

        // Sort duplicate groups by highest match percentage (descending)
        // Primary: Best match percentage in the group (higher = top)
        // Secondary: Fuzzy score (higher = top)
        // Tertiary: Number of matches in group (more matches = higher priority)
        var sortedDuplicates = duplicateGroups
            .Select(g => new 
            {
                Group = g,
                BestOverlap = g.Matches.Max(m => m.OverlapPercentage),
                BestFuzzy = g.Matches.Max(m => m.FuzzyScore),
                MatchCount = g.Matches.Count
            })
            .OrderByDescending(x => x.BestOverlap)
            .ThenByDescending(x => x.BestFuzzy)
            .ThenByDescending(x => x.MatchCount)
            .Select(x => new ScenarioDuplicateGroup(
                x.Group.BaseScenario,
                x.Group.Matches.OrderByDescending(m => m.OverlapPercentage).ThenByDescending(m => m.FuzzyScore).ToList(),
                x.Group.DuplicateType
            )
            {
                StepReuseScore = x.Group.StepReuseScore,
                SuggestScenarioOutline = x.Group.SuggestScenarioOutline,
                CommonBackgroundSteps = x.Group.CommonBackgroundSteps
            })
            .ToList();

        // N3 diagnostic: Superset/Subset were unreachable dead code before the containment fix,
        // so every containment group was previously labelled Exact. Post-fix, the Superset+Subset
        // count is exactly how many groups reclassified out of "Exact" on this run.
        var exactGroups = sortedDuplicates.Count(g => g.DuplicateType == ScenarioDuplicateType.Exact);
        var supersetGroups = sortedDuplicates.Count(g => g.DuplicateType == ScenarioDuplicateType.Superset);
        var subsetGroups = sortedDuplicates.Count(g => g.DuplicateType == ScenarioDuplicateType.Subset);
        Console.WriteLine($"   Duplicate-type labels: Exact={exactGroups}, Superset={supersetGroups}, Subset={subsetGroups} " +
                          $"(containment fix reclassified {supersetGroups + subsetGroups} group(s) that would previously have been labelled Exact)");

        return (sortedDuplicates, stats);
    }

    private (int Comparisons, int Duplicates, int FuzzyMatches, int ParameterVariations, int StepReuse, int OutlineCandidates) ProcessScenariosParallel(
        List<ScenarioInfo> scenarios,
        ConcurrentBag<ScenarioDuplicateGroup> duplicateGroups,
        ConcurrentDictionary<string, byte> processedPairs,
        double threshold,
        int maxUniqueSteps,
        bool useBucketOptimization)
    {
        var comparisonCount = 0;
        var duplicatesFound = 0;
        var fuzzyMatches = 0;
        var parameterVariations = 0;
        var stepReuse = 0;
        var outlineCandidates = 0;

        // Create buckets based on step count and normalized fingerprint signature
        Dictionary<string, List<ScenarioInfo>> buckets;
        
        if (useBucketOptimization)
        {
            buckets = CreateScenarioBuckets(scenarios);
            Console.WriteLine($"      Created {buckets.Count} comparison buckets");
        }
        else
        {
            buckets = new Dictionary<string, List<ScenarioInfo>> { ["all"] = scenarios };
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _settings.Analysis.MaxDegreeOfParallelism
        };

        foreach (var bucket in buckets.Values.Where(b => b.Count >= 2))
        {
            var bucketScenarios = bucket.ToList();
            var matchesByScenario = new ConcurrentDictionary<int, ConcurrentBag<ScenarioMatch>>();

            Parallel.For(0, bucketScenarios.Count, parallelOptions, i =>
            {
                var baseScenario = bucketScenarios[i];

                for (int j = i + 1; j < bucketScenarios.Count; j++)
                {
                    var compareScenario = bucketScenarios[j];

                    // Never compare a scenario against itself (guards against the same file being
                    // scanned twice, which would surface as a scenario "duplicated" with itself).
                    if (compareScenario.FeatureFilePath == baseScenario.FeatureFilePath &&
                        compareScenario.ScenarioLineNumber == baseScenario.ScenarioLineNumber)
                        continue;

                    Interlocked.Increment(ref comparisonCount);

                    // Early termination: step count difference too large
                    var stepCountDiff = Math.Abs(baseScenario.StepFingerprints.Count - compareScenario.StepFingerprints.Count);
                    var maxSteps = Math.Max(baseScenario.StepFingerprints.Count, compareScenario.StepFingerprints.Count);

                    if (stepCountDiff > maxSteps * (1 - threshold / 100.0))
                        continue;

                    var pairKey = string.Compare(baseScenario.ScenarioFingerprint, compareScenario.ScenarioFingerprint) < 0
                        ? $"{baseScenario.ScenarioFingerprint}|{compareScenario.ScenarioFingerprint}"
                        : $"{compareScenario.ScenarioFingerprint}|{baseScenario.ScenarioFingerprint}";

                    if (!processedPairs.TryAdd(pairKey, 0))
                        continue;

                    // Quick overlap check using fingerprint sets
                    var baseSet = new HashSet<string>(baseScenario.StepFingerprints);
                    var quickMatchCount = compareScenario.StepFingerprints.Count(f => baseSet.Contains(f));
                    var minStepsForMatch = Math.Min(baseScenario.StepFingerprints.Count, compareScenario.StepFingerprints.Count);
                    var quickOverlap = minStepsForMatch > 0 ? (double)quickMatchCount / minStepsForMatch * 100 : 0;

                    if (quickOverlap < threshold - 20 && !_settings.Analysis.EnableFuzzyMatching)
                        continue;

                    try
                    {
                        var match = CompareEnhancedScenarios(baseScenario, compareScenario);

                        if (match.OverlapPercentage >= threshold ||
                            (_settings.Analysis.EnableFuzzyMatching && match.FuzzyScore >= threshold))
                        {
                            var uniqueInBase = match.UniqueToBase.Count;
                            var uniqueInMatch = match.UniqueToMatch.Count;

                            if (uniqueInBase <= maxUniqueSteps && uniqueInMatch <= maxUniqueSteps)
                            {
                                var matches = matchesByScenario.GetOrAdd(i, _ => new ConcurrentBag<ScenarioMatch>());
                                matches.Add(match);

                                if (match.MatchType == MatchType.FuzzyMatch)
                                    Interlocked.Increment(ref fuzzyMatches);
                                if (match.MatchType == MatchType.ParameterVariation)
                                    Interlocked.Increment(ref parameterVariations);
                                Interlocked.Add(ref stepReuse, match.PotentialStepReuse);
                            }
                        }
                    }
                    catch
                    {
                        // Silently continue on comparison errors in parallel processing
                    }
                }
            });

            // Create duplicate groups from matches
            foreach (var kvp in matchesByScenario)
            {
                var baseScenario = bucketScenarios[kvp.Key];
                var matches = kvp.Value.ToList();

                if (matches.Any())
                {
                    try
                    {
                        var (duplicateType, suggestOutline, commonBackground) = AnalyzeDuplicateGroup(baseScenario, matches);
                        var stepReuseScore = matches.Sum(m => m.PotentialStepReuse);

                        if (suggestOutline)
                            Interlocked.Increment(ref outlineCandidates);

                        duplicateGroups.Add(new ScenarioDuplicateGroup(
                            baseScenario,
                            matches.OrderByDescending(m => m.OverlapPercentage).ThenByDescending(m => m.FuzzyScore).ToList(),
                            duplicateType
                        )
                        {
                            StepReuseScore = stepReuseScore,
                            SuggestScenarioOutline = suggestOutline,
                            CommonBackgroundSteps = commonBackground
                        });
                        Interlocked.Increment(ref duplicatesFound);
                    }
                    catch
                    {
                        // Silently continue on group analysis errors
                    }
                }
            }
        }

        return (comparisonCount, duplicatesFound, fuzzyMatches, parameterVariations, stepReuse, outlineCandidates);
    }

    private (int Comparisons, int Duplicates, int FuzzyMatches, int ParameterVariations, int StepReuse, int OutlineCandidates) ProcessScenariosSequential(
        List<ScenarioInfo> scenarios,
        ConcurrentBag<ScenarioDuplicateGroup> duplicateGroups,
        ConcurrentDictionary<string, byte> processedPairs,
        double threshold,
        int maxUniqueSteps)
    {
        var comparisonCount = 0;
        var duplicatesFound = 0;
        var fuzzyMatches = 0;
        var parameterVariations = 0;
        var stepReuse = 0;
        var outlineCandidates = 0;

        for (int i = 0; i < scenarios.Count; i++)
        {
            var baseScenario = scenarios[i];
            var matches = new List<ScenarioMatch>();

            for (int j = i + 1; j < scenarios.Count; j++)
            {
                var compareScenario = scenarios[j];

                // Never compare a scenario against itself (guards against the same file being
                // scanned twice, which would surface as a scenario "duplicated" with itself).
                if (compareScenario.FeatureFilePath == baseScenario.FeatureFilePath &&
                    compareScenario.ScenarioLineNumber == baseScenario.ScenarioLineNumber)
                    continue;

                comparisonCount++;

                var stepCountDiff = Math.Abs(baseScenario.StepFingerprints.Count - compareScenario.StepFingerprints.Count);
                var maxSteps = Math.Max(baseScenario.StepFingerprints.Count, compareScenario.StepFingerprints.Count);

                if (stepCountDiff > maxSteps * (1 - threshold / 100.0))
                    continue;

                var pairKey = string.Compare(baseScenario.ScenarioFingerprint, compareScenario.ScenarioFingerprint) < 0
                    ? $"{baseScenario.ScenarioFingerprint}|{compareScenario.ScenarioFingerprint}"
                    : $"{compareScenario.ScenarioFingerprint}|{baseScenario.ScenarioFingerprint}";

                if (!processedPairs.TryAdd(pairKey, 0))
                    continue;

                var baseSet = new HashSet<string>(baseScenario.StepFingerprints);
                var quickMatchCount = compareScenario.StepFingerprints.Count(f => baseSet.Contains(f));
                var minStepsForMatch = Math.Min(baseScenario.StepFingerprints.Count, compareScenario.StepFingerprints.Count);
                var quickOverlap = minStepsForMatch > 0 ? (double)quickMatchCount / minStepsForMatch * 100 : 0;

                if (quickOverlap < threshold - 20 && !_settings.Analysis.EnableFuzzyMatching)
                    continue;

                try
                {
                    var match = CompareEnhancedScenarios(baseScenario, compareScenario);

                    if (match.OverlapPercentage >= threshold ||
                        (_settings.Analysis.EnableFuzzyMatching && match.FuzzyScore >= threshold))
                    {
                        var uniqueInBase = match.UniqueToBase.Count;
                        var uniqueInMatch = match.UniqueToMatch.Count;

                        if (uniqueInBase <= maxUniqueSteps && uniqueInMatch <= maxUniqueSteps)
                        {
                            matches.Add(match);

                            if (match.MatchType == MatchType.FuzzyMatch)
                                fuzzyMatches++;
                            if (match.MatchType == MatchType.ParameterVariation)
                                parameterVariations++;
                            stepReuse += match.PotentialStepReuse;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      Error comparing scenarios: {ex.Message}");
                }
            }

            if (matches.Any())
            {
                try
                {
                    var (duplicateType, suggestOutline, commonBackground) = AnalyzeDuplicateGroup(baseScenario, matches);
                    var stepReuseScore = matches.Sum(m => m.PotentialStepReuse);

                    if (suggestOutline)
                        outlineCandidates++;

                    duplicateGroups.Add(new ScenarioDuplicateGroup(
                        baseScenario,
                        matches.OrderByDescending(m => m.OverlapPercentage).ThenByDescending(m => m.FuzzyScore).ToList(),
                        duplicateType
                    )
                    {
                        StepReuseScore = stepReuseScore,
                        SuggestScenarioOutline = suggestOutline,
                        CommonBackgroundSteps = commonBackground
                    });
                    duplicatesFound++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      Error analyzing duplicate group: {ex.Message}");
                }
            }
        }

        return (comparisonCount, duplicatesFound, fuzzyMatches, parameterVariations, stepReuse, outlineCandidates);
    }

    /// <summary>
    /// Creates buckets of scenarios that are likely to be duplicates based on:
    /// 1. Step count (scenarios with very different step counts can't be high-overlap duplicates)
    /// 2. Normalized step signature (hash of first few normalized step fingerprints)
    /// </summary>
    private Dictionary<string, List<ScenarioInfo>> CreateScenarioBuckets(List<ScenarioInfo> scenarios)
    {
        var buckets = new Dictionary<string, List<ScenarioInfo>>();
        var threshold = _settings.Analysis.ScenarioOverlapThreshold;

        // Calculate the max step count difference allowed for threshold overlap
        // If threshold is 80%, then step counts can differ by at most 20% of max
        var maxStepDiffRatio = 1 - (threshold / 100.0);

        foreach (var scenario in scenarios)
        {
            // Create bucket keys based on step count ranges
            var stepCount = scenario.StepFingerprints.Count;
            
            // Calculate range of step counts this scenario could match
            var minMatchableSteps = (int)Math.Ceiling(stepCount * (threshold / 100.0));
            var maxMatchableSteps = (int)(stepCount / (threshold / 100.0));

            // Also create a signature from the first few normalized steps
            var signature = CreateStepSignature(scenario);

            // Create buckets for each step count this scenario could match
            for (int targetSteps = minMatchableSteps; targetSteps <= Math.Min(maxMatchableSteps, stepCount + 10); targetSteps++)
            {
                var bucketKey = $"{targetSteps}_{signature}";
                
                if (!buckets.TryGetValue(bucketKey, out var bucket))
                {
                    bucket = new List<ScenarioInfo>();
                    buckets[bucketKey] = bucket;
                }
                bucket.Add(scenario);
            }

            // Also add to a general bucket for this step count (catches scenarios with different signatures)
            var generalKey = $"general_{stepCount}";
            if (!buckets.TryGetValue(generalKey, out var generalBucket))
            {
                generalBucket = new List<ScenarioInfo>();
                buckets[generalKey] = generalBucket;
            }
            generalBucket.Add(scenario);
        }

        // Remove buckets with only one scenario and deduplicate scenario references
        var result = new Dictionary<string, List<ScenarioInfo>>();
        foreach (var kvp in buckets)
        {
            var distinctScenarios = kvp.Value.Distinct().ToList();
            if (distinctScenarios.Count >= 2)
            {
                result[kvp.Key] = distinctScenarios;
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a signature from the first few steps of a scenario for bucketing
    /// </summary>
    private string CreateStepSignature(ScenarioInfo scenario)
    {
        if (!scenario.StepFingerprints.Any())
            return "empty";

        // Take first 2-3 fingerprints and create a combined hash
        var fingerprints = scenario.StepFingerprints.Take(3).OrderBy(f => f);
        var combined = string.Join("|", fingerprints);
        
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(bytes)[..8]; // Short signature
    }

    private ScenarioMatch CompareEnhancedScenarios(ScenarioInfo baseScenario, ScenarioInfo compareScenario)
    {
        var baseFingerprints = new HashSet<string>(baseScenario.StepFingerprints);
        var compareFingerprints = new HashSet<string>(compareScenario.StepFingerprints);

        // Exact fingerprint matching
        var exactMatchingFingerprints = baseFingerprints.Intersect(compareFingerprints).ToList();
        var exactMatchCount = exactMatchingFingerprints.Count;

        // Parameter-agnostic matching (for steps not matched exactly)
        var fuzzyMatches = new List<FuzzyStepMatch>();
        var additionalMatches = 0;

        if (_settings.Analysis.EnableParameterAgnosticMatching || _settings.Analysis.EnableFuzzyMatching)
        {
            var unmatchedBase = baseScenario.NormalizedStepTexts
                .Select((text, idx) => new { text, idx })
                .Where(x => !exactMatchingFingerprints.Contains(baseScenario.StepFingerprints[x.idx]))
                .ToList();

            var unmatchedCompare = compareScenario.NormalizedStepTexts
                .Select((text, idx) => new { text, idx })
                .Where(x => !exactMatchingFingerprints.Contains(compareScenario.StepFingerprints[x.idx]))
                .ToList();

            var matchedCompareIndices = new HashSet<int>();

            foreach (var baseStep in unmatchedBase)
            {
                foreach (var compareStep in unmatchedCompare)
                {
                    if (matchedCompareIndices.Contains(compareStep.idx))
                        continue;

                    double similarity;

                    if (_settings.Analysis.EnableParameterAgnosticMatching && baseStep.text == compareStep.text)
                    {
                        // Parameter-agnostic exact match
                        similarity = 100;
                    }
                    else if (_settings.Analysis.EnableFuzzyMatching)
                    {
                        similarity = _normalizer.CalculateSimilarity(
                            baseScenario.StepTexts[baseStep.idx],
                            compareScenario.StepTexts[compareStep.idx]);
                    }
                    else
                    {
                        continue;
                    }

                    if (similarity >= _settings.Analysis.FuzzyMatchThreshold)
                    {
                        fuzzyMatches.Add(new FuzzyStepMatch(
                            baseScenario.StepTexts[baseStep.idx],
                            compareScenario.StepTexts[compareStep.idx],
                            similarity
                        ));
                        additionalMatches++;
                        matchedCompareIndices.Add(compareStep.idx);
                        break;
                    }
                }
            }
        }

        var totalMatchingSteps = exactMatchCount + additionalMatches;
        var minStepsCount = Math.Min(baseScenario.StepFingerprints.Count, compareScenario.StepFingerprints.Count);
        var overlapPercentage = minStepsCount > 0 ? Math.Round((double)exactMatchCount / minStepsCount * 100, 1) : 0;
        var fuzzyScore = minStepsCount > 0 ? Math.Round((double)totalMatchingSteps / minStepsCount * 100, 1) : 0;

        // Check sequence
        var sameSequence = CheckSameSequence(baseScenario.StepFingerprints, compareScenario.StepFingerprints, exactMatchingFingerprints);

        // Get matching step texts
        var matchingStepTexts = baseScenario.StepTexts
            .Where((text, idx) => exactMatchingFingerprints.Contains(baseScenario.StepFingerprints[idx]))
            .ToList();

        // Find unique steps
        var uniqueToBase = baseScenario.StepTexts
            .Where((text, idx) => !exactMatchingFingerprints.Contains(baseScenario.StepFingerprints[idx]) &&
                                 !fuzzyMatches.Any(f => f.BaseStep == text))
            .ToList();

        var uniqueToCompare = compareScenario.StepTexts
            .Where((text, idx) => !exactMatchingFingerprints.Contains(compareScenario.StepFingerprints[idx]) &&
                                 !fuzzyMatches.Any(f => f.MatchStep == text))
            .ToList();

        // Determine match type
        var matchType = DetermineMatchType(exactMatchCount, additionalMatches, fuzzyMatches);

        // Calculate potential step reuse
        var potentialStepReuse = totalMatchingSteps;

        return new ScenarioMatch(
            compareScenario,
            overlapPercentage,
            exactMatchCount,
            baseScenario.StepFingerprints.Count,
            compareScenario.StepFingerprints.Count,
            sameSequence,
            matchingStepTexts,
            uniqueToBase,
            uniqueToCompare
        )
        {
            MatchType = matchType,
            FuzzyScore = fuzzyScore,
            PotentialStepReuse = potentialStepReuse,
            FuzzyMatches = fuzzyMatches
        };
    }

    private MatchType DetermineMatchType(int exactMatches, int fuzzyMatchCount, List<FuzzyStepMatch> fuzzyMatchDetails)
    {
        if (fuzzyMatchCount == 0)
            return MatchType.Exact;

        var hasParameterVariations = fuzzyMatchDetails.Any(f => f.Similarity == 100);
        var hasFuzzyMatches = fuzzyMatchDetails.Any(f => f.Similarity < 100);

        if (hasParameterVariations && hasFuzzyMatches)
            return MatchType.Mixed;

        if (hasParameterVariations)
            return MatchType.ParameterVariation;

        return MatchType.FuzzyMatch;
    }

    private (ScenarioDuplicateType Type, bool SuggestOutline, List<string> CommonBackground) AnalyzeDuplicateGroup(
        ScenarioInfo baseScenario, List<ScenarioMatch> matches)
    {
        var bestMatch = matches.OrderByDescending(m => m.OverlapPercentage).First();

        // Check if scenarios differ only by parameters (Scenario Outline candidate)
        var suggestOutline = matches.Any(m =>
            m.MatchType == MatchType.ParameterVariation &&
            m.OverlapPercentage >= 90);

        // Find common background steps (first N matching steps)
        var commonBackground = new List<string>();
        if (bestMatch.SameSequence && bestMatch.MatchingStepTexts.Count >= 2)
        {
            // First few matching steps could be moved to Background
            commonBackground = bestMatch.MatchingStepTexts.Take(Math.Min(3, bestMatch.MatchingStepTexts.Count)).ToList();
        }

        // Determine duplicate type.
        //
        // A "full match" means every step of the SMALLER scenario was matched exactly, which
        // makes OverlapPercentage (= exactMatchCount / min(stepCounts)) reach ~100. A full
        // match means two different things and must be labelled accordingly:
        //   - EQUAL step counts   -> the scenarios are identical                  -> Exact
        //   - UNEQUAL step counts -> the smaller is fully contained in the larger -> Superset/Subset
        // Containment is therefore evaluated BEFORE Exact, otherwise a 3-step scenario fully
        // inside a 5-step one scores 100% overlap and gets mislabelled "Exact" (the bug that
        // previously made the Superset/Subset branches unreachable dead code).
        ScenarioDuplicateType duplicateType;

        // base is fully contained in match (all base steps matched, match has more) ->
        // match contains all of base + more -> match is the superset of base.
        var baseContainedInMatch =
            bestMatch.MatchingSteps == bestMatch.TotalStepsBase &&
            bestMatch.TotalStepsMatch > bestMatch.TotalStepsBase;

        // match is fully contained in base (all match steps matched, base has more) ->
        // base contains all of match + more -> match is a subset of base.
        var matchContainedInBase =
            bestMatch.MatchingSteps == bestMatch.TotalStepsMatch &&
            bestMatch.TotalStepsBase > bestMatch.TotalStepsMatch;

        if (baseContainedInMatch)
            duplicateType = ScenarioDuplicateType.Superset;
        else if (matchContainedInBase)
            duplicateType = ScenarioDuplicateType.Subset;
        else if (Math.Abs(bestMatch.OverlapPercentage - 100) < 0.1 &&
                 bestMatch.TotalStepsBase == bestMatch.TotalStepsMatch)
            duplicateType = ScenarioDuplicateType.Exact;
        else if (bestMatch.MatchType == MatchType.ParameterVariation)
            duplicateType = ScenarioDuplicateType.ParameterVariation;
        else if (bestMatch.MatchType == MatchType.FuzzyMatch)
            duplicateType = ScenarioDuplicateType.FuzzyMatch;
        else
            duplicateType = ScenarioDuplicateType.HighOverlap;

        return (duplicateType, suggestOutline, commonBackground);
    }

    private bool CheckSameSequence(List<string> baseSteps, List<string> compareSteps, List<string> matchingFingerprints)
    {
        if (!matchingFingerprints.Any())
            return false;

        var baseOrder = baseSteps
            .Where(fp => matchingFingerprints.Contains(fp))
            .ToList();

        var compareOrder = compareSteps
            .Where(fp => matchingFingerprints.Contains(fp))
            .ToList();

        return baseOrder.SequenceEqual(compareOrder);
    }

    private List<string> GetProjectFolders(string root)
    {
        if (_settings.Analysis.ProjectFolders != null && _settings.Analysis.ProjectFolders.Any())
        {
            return _settings.Analysis.ProjectFolders
                .Select(p => Path.IsPathRooted(p)
                    ? p
                    : Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)))
                .Where(Directory.Exists)
                .ToList();
        }
        return Directory.GetDirectories(root).ToList();
    }

    private (HashSet<string> Placeholders, bool HasExamples) FindExamplesInfo(string[] lines, int startIndex, GherkinDialect dialect)
    {
        var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasExamples = false;

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (dialect.IsExamples(line))
            {
                hasExamples = true;
                
                // Get header row (first | row after Examples:)
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var headerLine = lines[j].Trim();
                    if (headerLine.StartsWith("|"))
                    {
                        var cols = headerLine.Split('|', StringSplitOptions.RemoveEmptyEntries)
                                            .Select(c => c.Trim())
                                            .Where(c => !string.IsNullOrEmpty(c))
                                            .ToList();
                        foreach (var col in cols)
                        {
                            placeholders.Add(col);
                        }
                        break; // Only need header row
                    }
                    if (!string.IsNullOrWhiteSpace(headerLine) && !headerLine.StartsWith("#"))
                        break;
                }
                break;
            }
            
            // Stop at next scenario
            if (dialect.IsScenarioStart(line))
            {
                break;
            }
        }
        
        return (placeholders, hasExamples);
    }

    private bool IsStepLine(string line, IReadOnlyList<string> stepKeywords)
    {
        return stepKeywords.Any(k =>
            line.StartsWith(k + " ", StringComparison.OrdinalIgnoreCase));
    }

    private (List<Dictionary<string, string>> Table, List<string> TableLines, int LinesConsumed) ParseDataTableWithLines(string[] lines, int startIndex)
    {
        var table = new List<Dictionary<string, string>>();
        var tableLines = new List<string>();
        var linesConsumed = 0;
        
        if (startIndex >= lines.Length || !lines[startIndex].Trim().StartsWith("|"))
            return (table, tableLines, linesConsumed);
        
        var headerLine = lines[startIndex].Trim();
        var headers = headerLine.Split('|', StringSplitOptions.RemoveEmptyEntries)
                               .Select(h => h.Trim())
                               .ToList();
        tableLines.Add(headerLine);
        linesConsumed++;
        
        int idx = startIndex + 1;
        while (idx < lines.Length && lines[idx].Trim().StartsWith("|"))
        {
            var rowLine = lines[idx].Trim();
            var values = rowLine.Split('|', StringSplitOptions.RemoveEmptyEntries)
                               .Select(v => v.Trim())
                               .ToList();
            var row = new Dictionary<string, string>();
            for (int c = 0; c < headers.Count && c < values.Count; c++)
            {
                row[headers[c]] = values[c];
            }
            table.Add(row);
            tableLines.Add(rowLine);
            idx++;
            linesConsumed++;
        }
        
        return (table, tableLines, linesConsumed);
    }

    private string GenerateFingerprint(string stepText, List<Dictionary<string, string>> dataTable)
    {
        var normalized = NormalizeStepText(stepText);
        var sb = new StringBuilder();
        sb.Append(normalized);
        
        if (dataTable != null && dataTable.Count > 0)
        {
            foreach (var row in dataTable)
            {
                foreach (var kvp in row.OrderBy(k => k.Key))
                    sb.Append('|').Append(kvp.Key).Append('=').Append(kvp.Value);
            }
        }
        
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(bytes)[..20];
    }

    private string NormalizeStepText(string stepText)
    {
        return _normalizer.NormalizeKeyword(stepText);
    }

    private List<DataError> CheckForUndefinedPlaceholders(
        string project, string filePath, string scenario, string stepText, int stepLine,
        List<string> tableLines, HashSet<string> definedPlaceholders)
    {
        var errors = new List<DataError>();
        var undefined = new List<string>();
        
        // Check step text for placeholders
        var matches = PlaceholderRegex.Matches(stepText);
        foreach (Match m in matches)
        {
            var name = m.Groups[1].Value;
            if (!ValidPlaceholderNameRegex.IsMatch(name)) 
                continue; // Skip invalid placeholder names like <-1>
            if (!definedPlaceholders.Contains(name))
                undefined.Add(name);
        }
        
        // Check table lines for placeholders
        foreach (var tableLine in tableLines)
        {
            var tableMatches = PlaceholderRegex.Matches(tableLine);
            foreach (Match m in tableMatches)
            {
                var name = m.Groups[1].Value;
                if (!ValidPlaceholderNameRegex.IsMatch(name)) 
                    continue;
                if (!definedPlaceholders.Contains(name) && !undefined.Contains(name))
                    undefined.Add(name);
            }
        }
        
        if (undefined.Any())
        {
            errors.Add(new DataError(
                project,
                Path.GetFileName(filePath),
                filePath,
                scenario,
                stepText,
                stepLine,
                undefined,
                definedPlaceholders.ToList(),
                DataErrorType.UndefinedPlaceholder,
                definedPlaceholders.Any()
            ));
        }
        
        return errors;
    }

    private StepReport GenerateStepReport(List<StepOccurrence> steps)
    {
        var report = new StepReport();
        
        if (!steps.Any())
            return report;

        // Filter excluded keywords
        var stepsForAnalysis = steps
            .Where(s => !_settings.Analysis.ExcludeKeywordsFromDuplicates.Any(k => 
                s.OriginalStepText.StartsWith(k + " ", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Group by fingerprint for exact duplicates
        var byFingerprint = stepsForAnalysis
            .GroupBy(s => s.Step.DataFingerprint)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in byFingerprint)
        {
            if (_reviewedFingerprints.Contains(group.Key))
                continue;

            var projects = group.Select(s => s.Project).Distinct().ToList();
            var sample = group.First();

            var duplicateGroup = new DuplicateGroup(
                sample.Step.StepText,
                sample.Step.DataFingerprint,
                sample.Step.DataTable,
                group.ToList(),
                DuplicateCategory.ExactDuplicate
            );

            if (projects.Count > 1)
            {
                report.CrossProjectDuplicates.Add(duplicateGroup);
            }
            else
            {
                report.WithinProjectDuplicates.Add(duplicateGroup);
            }
        }

        // Sort step duplicates by occurrence count (most duplicates first)
        report.CrossProjectDuplicates = report.CrossProjectDuplicates
            .OrderByDescending(g => g.Occurrences.Count)
            .ThenBy(g => g.StepPattern)
            .ToList();

        report.WithinProjectDuplicates = report.WithinProjectDuplicates
            .OrderByDescending(g => g.Occurrences.Count)
            .ThenBy(g => g.StepPattern)
            .ToList();

        // Group by step text for data variations
        var byStepText = stepsForAnalysis
            .GroupBy(s => s.Step.StepText)
            .Where(g => g.Select(x => x.Step.DataFingerprint).Distinct().Count() > 1)
            .ToList();

        foreach (var group in byStepText)
        {
            report.SameStepDifferentData.Add(new StepVariationsGroup(
                group.Key,
                group.GroupBy(s => s.Step.DataFingerprint)
                     .Select(g => new DataVariation(g.First().Step.DataTable, g.ToList()))
                     .OrderByDescending(v => v.Occurrences.Count)
                     .ToList()
            ));
        }

        // Sort data variations by total occurrences
        report.SameStepDifferentData = report.SameStepDifferentData
            .OrderByDescending(g => g.Variations.Sum(v => v.Occurrences.Count))
            .ToList();

        return report;
    }

    private class StepReport
    {
        public List<DuplicateGroup> CrossProjectDuplicates { get; set; } = new();
        public List<DuplicateGroup> WithinProjectDuplicates { get; set; } = new();
        public List<StepVariationsGroup> SameStepDifferentData { get; set; } = new();
    }
}

public class AnalysisStats
{
    public int FuzzyMatches { get; set; }
    public int ParameterVariations { get; set; }
    public int PotentialStepReuse { get; set; }
    public int ScenarioOutlineCandidates { get; set; }
}
