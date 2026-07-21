using System.Text.RegularExpressions;
using SpecHygiene.Analysis.Reqnroll;
using SpecHygiene.Models;

namespace SpecHygiene.Analysis;

/// <summary>
/// Analyzes step definition coverage and identifies unused step definitions
/// </summary>
public class StepDefinitionCoverageAnalyzer
{
    private readonly StepDefinitionParser _parser;
    private readonly List<string> _ignoreTags;
    private readonly bool _includeIgnoredScenariosInCoverage;
    private readonly IReadOnlyDictionary<string, string>? _parameterTypeFragments;
    private readonly bool _useSyntacticDiscovery;

    /// <summary>Accepted-dead bindings from the known-issues CSV. Empty when none is supplied.</summary>
    private IReadOnlyList<KnownIssue> _knownIssues = Array.Empty<KnownIssue>();

    /// <summary>Enum / transform fragments found in source by the discovery pass (R6/R7).</summary>
    private IReadOnlyDictionary<string, string>? _discoveredFragments;

    /// <summary>Step methods whose [Binding] status could not be confirmed — never dead (R11).</summary>
    private List<(StepDefinitionInfo Binding, string Reason)> _discoveryUnconfirmed = new();

    /// <summary>
    /// Bindings whose pattern could not be evaluated this run (unresolved {CustomType}, invalid regex,
    /// method-name convention). Held out of the unused list: "we could not tell" is not "unused".
    /// </summary>
    private List<CompiledBinding> _indeterminate = new();
    
    private static readonly Regex ScenarioRegex = new(
        @"^\s*(Scenario|Scenario Outline):\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    private static readonly Regex TagRegex = new(
        @"@[\w-]+",
        RegexOptions.Compiled);
    
    private static readonly string[] StepKeywords = { "Given ", "When ", "Then ", "And ", "But ", "* " };

    /// <summary>
    /// Creates a new StepDefinitionCoverageAnalyzer
    /// </summary>
    /// <param name="ignoreTags">Tags that mark scenarios to ignore (e.g., @ignore, @skip)</param>
    /// <param name="includeIgnoredScenariosInCoverage">If true, steps from ignored scenarios still count as "used" for coverage. 
    /// This prevents step definitions from being marked as "dead code" when they're only used in temporarily disabled scenarios.</param>
    /// <param name="parameterTypeFragments">Cucumber parameter-type regex fragments. Null uses
    /// Reqnroll's built-ins only; the Roslyn pass supplies custom enum / transform types on top.</param>
    /// <param name="useSyntacticDiscovery">Find bindings by parsing C# syntax (Roslyn) rather than by
    /// regex. Default. It gates on [Binding] (honouring inheritance), sees bare [Given]
    /// method-name-convention bindings the regex could not express, reads verbatim/escaped string
    /// literals correctly, and resolves enum / transform parameter types from source. Set false to fall
    /// back to the legacy regex parser — kept only so a surprising diff can be attributed.</param>
    public StepDefinitionCoverageAnalyzer(List<string>? ignoreTags = null, bool includeIgnoredScenariosInCoverage = true,
                                          IReadOnlyDictionary<string, string>? parameterTypeFragments = null,
                                          bool useSyntacticDiscovery = true,
                                          string? knownIssuesCsvPath = null)
    {
        _parser = new StepDefinitionParser();
        _ignoreTags = ignoreTags ?? new List<string> { "@ignore", "@skip", "@wip", "@pending", "@manual", "@disabled" };
        _includeIgnoredScenariosInCoverage = includeIgnoredScenariosInCoverage;
        _parameterTypeFragments = parameterTypeFragments;
        _useSyntacticDiscovery = useSyntacticDiscovery;

        // Known-issues CSV is optional; an unreadable or absent file simply means no rows, never a
        // failed run. It can only ever RECLASSIFY an unused binding, never hide or create one.
        if (!string.IsNullOrWhiteSpace(knownIssuesCsvPath) && File.Exists(knownIssuesCsvPath))
        {
            try { _knownIssues = KnownIssues.Parse(File.ReadAllText(knownIssuesCsvPath)); }
            catch (IOException) { _knownIssues = Array.Empty<KnownIssue>(); }
        }
    }

    /// <summary>
    /// Analyzes step definition coverage across the given directories
    /// </summary>
    public StepDefinitionCoverageReport AnalyzeCoverage(
        IEnumerable<string> stepDefinitionDirectories,
        IEnumerable<string> featureFileDirectories)
    {
        Console.WriteLine();
        Console.WriteLine("???????????????????????????????????????????????????????????????");
        Console.WriteLine("   ?? Step Definition Coverage Analysis");
        Console.WriteLine("???????????????????????????????????????????????????????????????");
        Console.WriteLine();
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        // Phase 1: Parse step definitions
        Console.WriteLine("Phase 1/4: Scanning for step definitions...");
        var stepDefinitions = ParseStepDefinitionsWithProgress(stepDefinitionDirectories.ToList());
        Console.WriteLine($"         ? Found {stepDefinitions.Count} step definitions ({sw.ElapsedMilliseconds}ms)");
        Console.WriteLine();

        // Phase 2: Collect feature file steps
        Console.WriteLine("Phase 2/4: Parsing feature files...");
        var (featureSteps, ignoredScenarios) = CollectFeatureStepsWithProgress(featureFileDirectories.ToList());
        Console.WriteLine($"         ? Found {featureSteps.Count} steps in feature files ({sw.ElapsedMilliseconds}ms)");
        Console.WriteLine($"         ? Found {ignoredScenarios.Count} ignored/skipped scenarios");
        Console.WriteLine();

        // Phase 3: Match steps to definitions
        Console.WriteLine("Phase 3/4: Matching steps to definitions...");
        MatchStepsToDefinitionsWithProgress(stepDefinitions, featureSteps);
        Console.WriteLine($"         ? Matching complete ({sw.ElapsedMilliseconds}ms)");
        Console.WriteLine();

        // Phase 4: Build report
        Console.WriteLine("Phase 4/4: Building coverage report...");
        var report = BuildCoverageReport(stepDefinitions, featureSteps, ignoredScenarios);
        sw.Stop();
        
        Console.WriteLine();
        Console.WriteLine("???????????????????????????????????????????????????????????????");
        Console.WriteLine("   Coverage Analysis Results:");
        Console.WriteLine("???????????????????????????????????????????????????????????????");
        Console.WriteLine($"   ?? Coverage: {report.CoveragePercentage:F1}% ({report.UsedStepDefinitions}/{report.TotalStepDefinitions} definitions used)");
        Console.WriteLine($"   ???  Unused step definitions: {report.UnusedStepDefinitions}");
        if (report.KnownIssueDefinitions.Count > 0)
            Console.WriteLine($"        of which accepted (known issues): {report.KnownIssueDefinitions.Count} - actionable: {report.ActionableUnusedDefinitions.Count}");
        if (report.IndeterminateDefinitions.Count > 0)
            Console.WriteLine($"   NOTE: {report.IndeterminateDefinitions.Count} definition(s) could not be evaluated - NOT counted as unused");
        Console.WriteLine($"   ?? Duplicate definitions: {report.DuplicateDefinitions.Count}");
        Console.WriteLine($"   ??  Ignored scenarios: {report.IgnoredScenarios.Count}");
        Console.WriteLine($"   ?? Single-use definitions: {report.SingleUseDefinitions.Count}");
        Console.WriteLine($"   ??  Total time: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine("???????????????????????????????????????????????????????????????");
        Console.WriteLine();

        return report;
    }

    /// <summary>
    /// Parses step definitions with progress reporting
    /// </summary>
    private List<StepDefinitionInfo> ParseStepDefinitionsWithProgress(List<string> directories)
    {
        var stepDefinitions = new List<StepDefinitionInfo>();
        var allCsFiles = new List<string>();

        // Collect all C# files first
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                Console.WriteLine($"         ??  Directory not found: {directory}");
                continue;
            }

            var csFiles = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
                .ToList();
            
            allCsFiles.AddRange(csFiles);
        }

        Console.WriteLine($"         Scanning {allCsFiles.Count} C# files...");

        if (_useSyntacticDiscovery)
        {
            var found = new SyntacticBindingDiscovery().Discover(allCsFiles);

            // Enum + [StepArgumentTransformation] fragments discovered in source (R6/R7). These are
            // what shrink the indeterminate bucket: without them every {CustomType} pattern is
            // unevaluable. Explicit fragments passed to the ctor still win.
            _discoveredFragments = found.ParameterTypeFragments;

            // [Binding] unconfirmable (base class declared outside the scanned source). Reqnroll may or
            // may not register these, so they are held out of the unused list rather than guessed at.
            _discoveryUnconfirmed = found.Unconfirmed;

            // Finding NOTHING in a tree that clearly has step files is a configuration/gating failure,
            // not a result. Say so loudly and name the likely cause rather than reporting an empty run
            // as if the suite had no bindings.
            if (found.Bindings.Count == 0 && allCsFiles.Count > 0)
            {
                Console.WriteLine($"         WARNING: 0 step definitions found across {allCsFiles.Count} C# file(s).");
                if (found.Unconfirmed.Count > 0)
                    Console.WriteLine($"         WARNING: {found.Unconfirmed.Count} step method(s) found but [Binding] could not be confirmed - their base class is probably outside the scanned paths. Add the project declaring the [Binding] base to CoverageAnalysis.StepDefinitionPaths, or set CoverageAnalysis.UseSyntacticDiscovery=false to fall back to the regex parser.");
                else
                    Console.WriteLine("         WARNING: no [Given]/[When]/[Then] attributes seen at all - check CoverageAnalysis.StepDefinitionPaths points at the step-definition projects.");
            }

            Console.WriteLine($"         OK Found {found.Bindings.Count} step definitions (Roslyn syntax)");
            Console.WriteLine($"         {found.ParameterTypeFragments.Count - Reqnroll.DefaultCucumberExpressionParameterTypes.Fragments.Count} custom parameter type(s) resolved from source");
            if (found.Unconfirmed.Count > 0)
                Console.WriteLine($"         NOTE: {found.Unconfirmed.Count} step method(s) with unconfirmable [Binding] - held out of the unused list");
            return found.Bindings;
        }

        var processed = 0;
        var foundCount = 0;
        var lastProgress = 0;

        foreach (var filePath in allCsFiles)
        {
            try
            {
                var definitions = _parser.ParseFile(filePath);
                if (definitions.Any())
                {
                    stepDefinitions.AddRange(definitions);
                    foundCount += definitions.Count;
                }
            }
            catch (Exception ex)
            {
                // Silently skip files that can't be parsed
            }


            processed++;
            var progress = (int)((processed * 100.0) / allCsFiles.Count);
            
            // Update progress every 10%
            if (progress >= lastProgress + 10)
            {
                Console.WriteLine($"         Progress: {progress}% ({processed}/{allCsFiles.Count} files, {foundCount} definitions found)");
                lastProgress = progress;
            }
        }
        
        
        
        
        Console.WriteLine(); // New line after progress

        return stepDefinitions;
    }

    /// <summary>
    /// Collects all steps from feature files with progress reporting
    /// </summary>
    private (List<FeatureStep> Steps, List<IgnoredScenario> IgnoredScenarios) CollectFeatureStepsWithProgress(
        List<string> directories)
    {
        var steps = new List<FeatureStep>();
        var ignoredScenarios = new List<IgnoredScenario>();
        var allFeatureFiles = new List<string>();


        Console.WriteLine($"         Searching for feature files in {directories.Count} directories:");
        foreach (var dir in directories)
        {
            Console.WriteLine($"           - {dir}");
        }

        // Collect all feature files first
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                Console.WriteLine($"         ??  Directory not found: {directory}");
                continue;
            }

            var featureFiles = Directory.GetFiles(directory, "*.feature", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
                .ToList();
            
            Console.WriteLine($"         Found {featureFiles.Count} feature files in {Path.GetFileName(directory)}");
            allFeatureFiles.AddRange(featureFiles);
        }

        Console.WriteLine($"         Parsing {allFeatureFiles.Count} total feature files...");

        var processed = 0;
        var lastProgress = 0;
        var stepsPerProject = new Dictionary<string, int>();

        foreach (var filePath in allFeatureFiles)
        {
            try
            {
                var (fileSteps, fileIgnored) = ParseFeatureFile(filePath);
                
                // Track steps per project for diagnostic
                var projectName = ExtractProjectName(filePath);
                if (!stepsPerProject.ContainsKey(projectName))
                    stepsPerProject[projectName] = 0;
                stepsPerProject[projectName] += fileSteps.Count;
                
                steps.AddRange(fileSteps);
                ignoredScenarios.AddRange(fileIgnored);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"         ??  Failed to parse: {Path.GetFileName(filePath)} - {ex.Message}");
            }

            processed++;
            var progress = (int)((processed * 100.0) / allFeatureFiles.Count);
            
            if (progress >= lastProgress + 10)
            {
                Console.WriteLine($"         Progress: {progress}% ({processed}/{allFeatureFiles.Count} files, {steps.Count} steps found)");
                lastProgress = progress;
            }
        }
        
        Console.WriteLine(); // New line after progress

        return (steps, ignoredScenarios);
    }

    /// <summary>
    /// Matches feature steps to step definitions with progress
    /// Matches on Reqnroll's semantics: full text match AND keyword compatibility (a [Given] binding
    /// is only callable by a step whose resolved keyword is Given; [StepDefinition] binds any).
    /// </summary>
    private void MatchStepsToDefinitionsWithProgress(List<StepDefinitionInfo> definitions, List<FeatureStep> steps)
    {
        if (!steps.Any() || !definitions.Any())
        {
            Console.WriteLine("         NOTE: no steps or definitions to match");
            return;
        }

        Console.WriteLine($"         Matching {steps.Count} steps against {definitions.Count} definitions...");
        Console.WriteLine($"         (Reqnroll semantics: full match, grammar auto-detected, keyword-compatible)");

        // Route + compile every pattern ONCE. Anything that cannot be evaluated becomes indeterminate
        // and is held out of the dead list entirely — see BindingMatcher.
        var matcher = new BindingMatcher(_parameterTypeFragments ?? _discoveredFragments);
        var compiled = definitions.Select(matcher.Compile).ToList();
        _indeterminate = compiled.Where(c => c.IsIndeterminate).ToList();
        var evaluable = compiled.Where(c => !c.IsIndeterminate).ToList();

        if (_indeterminate.Count > 0)
            Console.WriteLine($"         NOTE: {_indeterminate.Count} definition(s) could not be evaluated - held out of the unused list");

        // Fast path holds ONLY literal-text bindings, because a lookup keyed on pattern text can never
        // find a parametric one: "I wait {int} seconds" is bound by the step "I wait 5 seconds", whose
        // key is "i wait 5 seconds". Parametric bindings are therefore always scanned, and the two
        // results are UNIONed. Previously the lookup short-circuited the scan whenever a literal
        // binding matched, so a parametric binding whose only usages were also covered by literal
        // bindings was reported dead even though Reqnroll binds it — a false dead.
        var literalLookup = new Dictionary<string, List<StepDefinitionInfo>>(StringComparer.Ordinal);
        foreach (var def in evaluable.Where(c => c.IsLiteral).Select(c => c.Definition))
        {
            var normalizedPattern = NormalizeStepKey(def.Pattern);
            if (!literalLookup.ContainsKey(normalizedPattern))
            {
                literalLookup[normalizedPattern] = new List<StepDefinitionInfo>();
            }
            literalLookup[normalizedPattern].Add(def);
        }
        var parametric = evaluable.Where(c => !c.IsLiteral).ToList();

        // Index the parametric bindings by their required literal words so each step tests only
        // plausibly-matching bindings, not all of them. Bindings with no sound required word fall into
        // the index's always-scan bucket. The index only PRE-FILTERS; BindingMatcher.Matches below is
        // still the authority, so this changes speed, never verdicts.
        var index = new LiteralTokenIndex(parametric);
        Console.WriteLine($"         {literalLookup.Count:N0} literal binding key(s); {parametric.Count:N0} parametric ({index.IndexedCount:N0} indexed, {index.AlwaysScanCount:N0} always-scanned per step)");

        // Phase 3/4 is the hot path. Two changes keep it fast without changing results:
        //  (1) Raise the regex cache so each definition pattern compiles ONCE. The default cache holds
        //      only 15 patterns, so with thousands of definitions it recompiles constantly.
        //  (2) Match each UNIQUE step text once — feature suites repeat the same step thousands of times,
        //      so grouping collapses ~167k steps down to a few thousand distinct matches.
        Regex.CacheSize = Math.Max(Regex.CacheSize, definitions.Count * 3 + 100);

        var stepGroups = new Dictionary<(string Text, StepKeyword? Keyword), List<FeatureStep>>();
        foreach (var step in steps)
        {
            var key = (NormalizeStepKey(step.StepText), step.ResolvedKeyword);
            if (!stepGroups.TryGetValue(key, out var list)) stepGroups[key] = list = new List<FeatureStep>();
            list.Add(step);
        }
        Console.WriteLine($"         {stepGroups.Count:N0} unique step texts (from {steps.Count:N0} steps)");

        // The scan is O(unique steps × parametric bindings) regex matches — the dominant cost. Split it:
        //  - PARALLEL read: compute each group's matching bindings. Matches only READS the compiled
        //    bindings (Regex instance methods are thread-safe) and IsKeywordCompatible is pure, so this
        //    is race-free. This is where the time goes, and it parallelises cleanly.
        //  - SERIAL apply: mutating def.UsageCount / step.IsMatched afterwards, single-threaded, so no
        //    locking on the hot path.
        var groupList = stepGroups.ToList();
        var groupMatches = new List<StepDefinitionInfo>?[groupList.Count];

        Console.WriteLine($"         Scanning {groupList.Count:N0} unique steps across {Environment.ProcessorCount} core(s)...");

        // Coarse progress from inside the parallel scan. Interlocked counter; a lock guards the ~20
        // actual prints so lines don't interleave (printing 20 times over a multi-second scan is free).
        var scanned = 0;
        var chunk = Math.Max(1, groupList.Count / 20);
        var printLock = new object();

        Parallel.For(0, groupList.Count, i =>
        {
            var group = groupList[i];
            var stepText = group.Value[0].StepText;
            var keyword = group.Key.Keyword;
            var matchingDefs = new List<StepDefinitionInfo>();

            // R2 gates BOTH paths: a [Given] binding is not callable by a When step, however well the
            // text matches. Applied to the literal lookup too, or literal bindings would bypass it.
            if (literalLookup.TryGetValue(group.Key.Text, out var exactMatches))
                matchingDefs.AddRange(exactMatches.Where(d => IsKeywordCompatible(d, keyword)));
            // Only the index's candidate bindings (required words all present) are tested — the same
            // Reqnroll semantics, on a pruned set. Once per unique step.
            var stepTokens = LiteralTokenIndex.Tokenize(stepText);
            matchingDefs.AddRange(index.Candidates(stepTokens)
                                       .Where(c => IsKeywordCompatible(c.Definition, keyword)
                                                && BindingMatcher.Matches(c, stepText))
                                       .Select(c => c.Definition));

            groupMatches[i] = matchingDefs.Count > 0 ? matchingDefs : null;

            var done = System.Threading.Interlocked.Increment(ref scanned);
            if (done % chunk == 0 || done == groupList.Count)
                lock (printLock)
                    Console.WriteLine($"         Matching: {(int)(done * 100L / groupList.Count)}% ({done:N0}/{groupList.Count:N0})");
        });
        Console.WriteLine();   // end the progress line

        var matched = 0;
        for (int i = 0; i < groupList.Count; i++)
        {
            if (groupMatches[i] is not { } defs) continue;
            foreach (var step in groupList[i].Value)
                MarkStepAsMatchedForAllDuplicates(step, defs, ref matched);
        }

        Console.WriteLine($"         Matched {matched:N0} of {steps.Count:N0} steps ({(matched * 100.0 / steps.Count):F1}%)");
    }



    // The previous eight-strategy match ladder lived here. BindingMatcher replaces it: each pattern is
    // routed to Reqnroll's real grammar (Cucumber Expression vs raw regex) and must match in FULL.
    // The ladder's Strategy 3 accepted an UNANCHORED partial match, so a binding for 'I do something'
    // was reported alive by an unrelated step 'I do something extra' that Reqnroll would never bind.
    
    
    
    

    /// <summary>
    /// Normalizes text for matching by removing keywords and trimming
    /// </summary>
    private string NormalizeForMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
            
        var result = text.Trim();
        
        // Remove Gherkin keywords
        foreach (var keyword in StepKeywords)
        {
            if (result.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(keyword.Length).Trim();
                break;
            }
        }
        
        // Remove regex anchors if present
        result = result.TrimStart('^').TrimEnd('$');
        
        return result;
    }


    /// <summary>
    /// Key for the literal fast path and for grouping identical step texts. CASE-PRESERVING, unlike
    /// the lowercasing key this replaced: Reqnroll matches Cucumber Expression literals
    /// case-sensitively (<see cref="Reqnroll.PatternCompiler"/> wraps only enum alternations in
    /// (?i:…), deliberately leaving the rest of the pattern case-sensitive). A case-insensitive key
    /// would make the fast path disagree with <see cref="Reqnroll.BindingMatcher"/>, and would merge
    /// step texts differing only by case into one group — whose scan only ever tests the first
    /// variant's text.
    /// </summary>
    private string NormalizeStepKey(string text) =>
        Regex.Replace(NormalizeForMatching(text), @"\s+", " ").Trim();


    /// <summary>
    /// Marks a step as matched and updates the definition usage
    /// </summary>
    private void MarkStepAsMatched(FeatureStep step, StepDefinitionInfo definition, ref int matchedCount)
    {
        step.IsMatched = true;
        step.MatchedDefinition = definition;
        definition.UsageCount++;
        definition.Usages.Add(new StepUsage
        {
            FeatureFile = step.FeatureFile,
            Scenario = step.Scenario,
            StepText = step.StepText,
            LineNumber = step.LineNumber,
            Project = step.Project
        });
        matchedCount++;
    }

    /// <summary>
    /// Marks a step as matched and updates usage for ALL definitions with the same pattern.
    /// This ensures that duplicate step definitions all show correct usage counts.
    /// </summary>
    private void MarkStepAsMatchedForAllDuplicates(FeatureStep step, List<StepDefinitionInfo> definitions, ref int matchedCount)
    {
        step.IsMatched = true;
        step.MatchedDefinition = definitions.First();
        
        // Update usage count for ALL definitions with the same pattern
        foreach (var definition in definitions)
        {
            definition.UsageCount++;
            definition.Usages.Add(new StepUsage
            {
                FeatureFile = step.FeatureFile,
                Scenario = step.Scenario,
                StepText = step.StepText,
                LineNumber = step.LineNumber,
                Project = step.Project
            });
        }
        matchedCount++;
    }

    /// <summary>
    /// Third-level matching: Normalize both step and pattern text for comparison
    /// This handles cases where regex matching fails but the text is semantically equivalent
    /// </summary>
    private bool TryNormalizedTextMatch(string stepText, string pattern)
    {
        try
        {
            // Normalize step: replace <placeholders>, 'quoted', "quoted", and numbers
            var normalizedStep = NormalizeQuotedValues(stepText);
            
            // Normalize pattern: convert regex to plain text with <PARAM> placeholders
            var normalizedPattern = NormalizePatternToText(pattern);
            
            // Direct comparison
            if (string.Equals(normalizedStep, normalizedPattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            // Also try with trimmed whitespace
            var trimmedStep = Regex.Replace(normalizedStep, @"\s+", " ").Trim();
            var trimmedPattern = Regex.Replace(normalizedPattern, @"\s+", " ").Trim();
            
            return string.Equals(trimmedStep, trimmedPattern, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a single feature file to extract steps and ignored scenarios
    /// </summary>
    private (List<FeatureStep> Steps, List<IgnoredScenario> IgnoredScenarios) ParseFeatureFile(string filePath)
    {
        var steps = new List<FeatureStep>();
        var ignoredScenarios = new List<IgnoredScenario>();

        var lines = File.ReadAllLines(filePath);
        var projectName = ExtractProjectName(filePath);
        var featureFileName = Path.GetFileName(filePath);

        // Lines before the first Background:/Scenario:/Examples: header are the Feature's free-text
        // narrative and are never steps, however they start.
        var insideBlock = GherkinNarrativeGuard.ComputeInsideBlockFlags(lines);

        var featureTags = new List<string>();
        var currentTags = new List<string>();
        var currentScenario = string.Empty;
        var inBackground = false;
        var isIgnored = false;

        // Scenario Outline handling: buffer the block's step lines, then re-emit them once per Examples
        // row with <placeholders> substituted. Without this, a step like "Given <count> items" never matches
        // a binding of "Given (-?\d+) items", so an outline-only binding is wrongly flagged unused.
        //
        // Buffering is NOT gated on the "Scenario Outline" keyword: Gherkin expands an Examples: table
        // identically under a plain "Scenario:" (confirmed against the real parser — the sample suite has hundreds of
        // such blocks), so keying on the keyword silently skipped every one of them. Blocks with no
        // Examples: table simply never expand, so buffering unconditionally costs nothing.
        var inExamples = false;
        string[]? exampleHeaders = null;
        var outlineSteps = new List<(string Text, string Full, int Line, StepKeyword? Keyword)>();

        // And/But/* inherit the nearest preceding concrete keyword in the same block; reset per block.
        StepKeyword? lastConcreteKeyword = null;

        void AddStep(string text, string full, int line, bool ignoredScenario, StepKeyword? keyword)
        {
            if (ignoredScenario && currentScenario != "Background" && !_includeIgnoredScenariosInCoverage) return;
            steps.Add(new FeatureStep
            {
                Project = projectName,
                FeatureFile = featureFileName,
                Scenario = currentScenario,
                StepText = text,
                FullStepText = full,
                LineNumber = line,
                IsMatched = false,
                IsFromIgnoredScenario = ignoredScenario,
                ResolvedKeyword = keyword
            });
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var lineNumber = i + 1;

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue;

            if (line.StartsWith("@"))
            {
                var tags = TagRegex.Matches(line).Select(m => m.Value).ToList();
                if (string.IsNullOrEmpty(currentScenario) && !inBackground) featureTags.AddRange(tags);
                else currentTags.AddRange(tags);
                continue;
            }

            if (line.StartsWith("Feature:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("Background:", StringComparison.OrdinalIgnoreCase))
            {
                inBackground = true; inExamples = false; outlineSteps.Clear(); lastConcreteKeyword = null;
                currentScenario = "Background"; currentTags.Clear();
                continue;
            }

            var scenarioMatch = ScenarioRegex.Match(line);
            if (scenarioMatch.Success)
            {
                inBackground = false;
                inExamples = false; exampleHeaders = null; outlineSteps.Clear(); lastConcreteKeyword = null;
                currentScenario = scenarioMatch.Groups[2].Value.Trim();

                var allTags = featureTags.Concat(currentTags).ToList();
                var ignoreTag = allTags.FirstOrDefault(t => _ignoreTags.Any(it => t.Equals(it, StringComparison.OrdinalIgnoreCase)));
                isIgnored = !string.IsNullOrEmpty(ignoreTag);
                if (isIgnored)
                    ignoredScenarios.Add(new IgnoredScenario
                    {
                        Project = projectName, FeatureFile = featureFileName, FeatureFilePath = filePath,
                        ScenarioName = currentScenario, LineNumber = lineNumber, Tags = allTags, IgnoreReason = ignoreTag!
                    });
                currentTags.Clear();
                continue;
            }

            if (line.StartsWith("Examples:", StringComparison.OrdinalIgnoreCase))
            {
                inExamples = true; exampleHeaders = null;
                continue;
            }

            if (line.StartsWith("|"))
            {
                if (inExamples)
                {
                    var cells = SplitTableRow(line);
                    if (exampleHeaders is null) exampleHeaders = cells;                 // header row
                    else foreach (var (text, full, ln, kw) in outlineSteps)             // data row → expand
                        AddStep(Substitute(text, exampleHeaders, cells), full, ln, isIgnored, kw);
                }
                // otherwise a data table under a step — ignore
                continue;
            }

            // Step-keyword grammar comes from GherkinNarrativeGuard, which is case-SENSITIVE — the real
            // parser never treats a lowercase "and"/"but" as a keyword. Combined with insideBlock, this
            // stops a Feature's free-text narrative ("...and compares against outbound payload" continuing
            // the previous sentence) being parsed as a step and reported undefined.
            //
            // The guard covers Given/When/Then/And/But but not the "* " asterisk step, which is valid
            // Gherkin and which our parser has always accepted. Keep it: dropping those lines would shrink
            // the corpus and turn live bindings dead — the one direction this change must never move.
            if (insideBlock[i] && !string.IsNullOrEmpty(currentScenario)
                && TryMatchStepLine(line, out var stepText, out var rawKeyword))
            {
                // Buffer every block's steps: an Examples: table may follow under either keyword.
                var resolved = ResolveKeyword(rawKeyword, ref lastConcreteKeyword);
                outlineSteps.Add((Text: stepText, Full: line, Line: lineNumber, Keyword: resolved));
                // Emit the raw template too (permissive safety net: a binding with a (.+)-style param
                // still matches the <placeholder> text, so we never miss a used binding). Expansion below
                // only ADDS rows, so making the corpus more accurate can never lose a match here.
                AddStep(stepText, line, lineNumber, isIgnored, resolved);
            }
        }

        return (steps, ignoredScenarios);
    }

    /// <summary>Split a Gherkin table row "| a | b |" into trimmed cell values.</summary>
    private static string[] SplitTableRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    /// <summary>Substitute &lt;header&gt; placeholders in an outline step with the Examples row values.</summary>
    private static string Substitute(string stepText, string[] headers, string[] values)
    {
        var result = stepText;
        for (int i = 0; i < headers.Length && i < values.Length; i++)
            result = result.Replace("<" + headers[i] + ">", values[i]);
        return result;
    }

    /// <summary>
    /// Real Gherkin step-line grammar (case-sensitive, per <see cref="GherkinNarrativeGuard"/>), plus the
    /// "* " asterisk step the guard doesn't model. Returns the step text without its keyword.
    /// </summary>
    private static bool TryMatchStepLine(string line, out string stepText, out string rawKeyword)
    {
        if (GherkinNarrativeGuard.TryMatchStepLine(line, out rawKeyword, out stepText)) return true;

        if (line.StartsWith("* ", StringComparison.Ordinal))
        {
            stepText = line.Substring(2).Trim();
            rawKeyword = "*";
            return stepText.Length > 0;
        }

        stepText = "";
        rawKeyword = "";
        return false;
    }

    /// <summary>
    /// Resolve a written keyword to the concrete one Reqnroll binds on. Given/When/Then are concrete
    /// and become the new inheritance anchor; And/But/* inherit the nearest preceding concrete keyword
    /// in the same block. Null when nothing precedes them — that step then matches keyword-agnostically,
    /// so a keyword we could not resolve can never itself make a binding look dead.
    /// </summary>
    private static StepKeyword? ResolveKeyword(string rawKeyword, ref StepKeyword? lastConcrete)
    {
        switch (rawKeyword)
        {
            case "Given": lastConcrete = StepKeyword.Given; return lastConcrete;
            case "When":  lastConcrete = StepKeyword.When;  return lastConcrete;
            case "Then":  lastConcrete = StepKeyword.Then;  return lastConcrete;
            default:      return lastConcrete;   // And / But / * inherit
        }
    }

    /// <summary>
    /// Map a parsed binding attribute to the kind <see cref="KeywordCompatibility"/> understands.
    /// [And]/[But] bindings have no Reqnroll equivalent kind, so they are treated as
    /// [StepDefinition] (matches any keyword) — the permissive choice, which can only preserve an
    /// alive verdict, never manufacture a dead one.
    /// </summary>
    private static BindingKind ToBindingKind(StepDefinitionType type) => type switch
    {
        StepDefinitionType.Given => BindingKind.Given,
        StepDefinitionType.When  => BindingKind.When,
        StepDefinitionType.Then  => BindingKind.Then,
        _                        => BindingKind.StepDefinition,
    };

    /// <summary>
    /// R2: would Reqnroll let this binding bind a step with this resolved keyword? An unresolved step
    /// keyword falls back to keyword-agnostic (always compatible).
    /// </summary>
    private static bool IsKeywordCompatible(StepDefinitionInfo def, StepKeyword? resolved) =>
        resolved is null || KeywordCompatibility.IsKeywordCompatible(ToBindingKind(def.Type), resolved.Value);


    /// <summary>
    /// Builds the coverage report from analysis results
    /// </summary>
    private StepDefinitionCoverageReport BuildCoverageReport(
        List<StepDefinitionInfo> definitions,
        List<FeatureStep> steps,
        List<IgnoredScenario> ignoredScenarios)
    {
        // A binding is reported unused only if we actually evaluated its pattern. Indeterminate ones
        // sit at UsageCount 0 for want of evaluation, not for want of usage — counting them as unused
        // would be a false-dead flood, so they get their own bucket and
        // used + unused + indeterminate == total holds.
        // Unconfirmable [Binding] joins the indeterminate bucket: Reqnroll may or may not register
        // those methods, so we can no more call them unused than we can call them used.
        foreach (var (b, reason) in _discoveryUnconfirmed)
            _indeterminate.Add(new CompiledBinding { Definition = b, IndeterminateReason = reason });

        var indeterminateDefs = new HashSet<StepDefinitionInfo>(_indeterminate.Select(c => c.Definition));
        var usedDefinitions = definitions.Where(d => d.UsageCount > 0).ToList();
        var unusedDefinitions = definitions.Where(d => d.UsageCount == 0 && !indeterminateDefs.Contains(d)).ToList();
        var singleUseDefinitions = definitions.Where(d => d.UsageCount == 1).ToList();

        // Split the unused list against the known-issues CSV. These are still unused - the CSV does not
        // dispute the verdict, it records that someone already triaged and accepted it. Separating them
        // keeps a run's actionable output to what is actually new.
        var knownIssueDefs = new List<KnownIssueStepDefinition>();
        var actionableUnused = new List<StepDefinitionInfo>();
        foreach (var d in unusedDefinitions.OrderBy(d => d.FilePath).ThenBy(d => d.LineNumber))
        {
            var comment = _knownIssues.Count == 0 ? null : KnownIssues.Match(_knownIssues, d.FilePath, d.MethodName);
            if (comment is null) actionableUnused.Add(d);
            else knownIssueDefs.Add(new KnownIssueStepDefinition { Definition = d, Comment = comment });
        }

        // Find duplicate step definitions (same pattern defined in multiple places)
        Console.WriteLine("         Finding duplicate step definitions...");
        var duplicateDefinitions = FindDuplicateDefinitions(definitions);
        Console.WriteLine($"         Found {duplicateDefinitions.Count} duplicate definition groups");

        var totalDefinitions = definitions.Count;
        var coveragePercentage = totalDefinitions > 0 
            ? (double)usedDefinitions.Count / totalDefinitions * 100 
            : 100;

        return new StepDefinitionCoverageReport
        {
            TotalStepDefinitions = totalDefinitions,
            UsedStepDefinitions = usedDefinitions.Count,
            UnusedStepDefinitions = unusedDefinitions.Count,
            CoveragePercentage = coveragePercentage,
            UnusedDefinitions = unusedDefinitions.OrderBy(d => d.FilePath).ThenBy(d => d.LineNumber).ToList(),
            KnownIssueDefinitions = knownIssueDefs,
            ActionableUnusedDefinitions = actionableUnused,
            MostUsedDefinitions = definitions.OrderByDescending(d => d.UsageCount).Take(20).ToList(),
            SingleUseDefinitions = singleUseDefinitions.OrderBy(d => d.FilePath).ThenBy(d => d.LineNumber).ToList(),
            IgnoredScenarios = ignoredScenarios.OrderBy(s => s.Project).ThenBy(s => s.FeatureFile).ToList(),
            DuplicateDefinitions = duplicateDefinitions,
            IndeterminateDefinitions = _indeterminate
                .Select(c => new IndeterminateStepDefinition { Definition = c.Definition, Reason = c.IndeterminateReason! })
                .OrderBy(x => x.Definition.FilePath).ThenBy(x => x.Definition.LineNumber).ToList(),
        };
    }

    /// <summary>
    /// Finds step definitions with the same pattern defined in multiple places.
    /// Excludes alias definitions (same pattern with different keywords on the same method).
    /// </summary>
    private List<DuplicateStepDefinitionGroup> FindDuplicateDefinitions(List<StepDefinitionInfo> definitions)
    {
        var duplicates = new List<DuplicateStepDefinitionGroup>();
        
        // Group by normalized pattern (ignore keywords, case-insensitive)
        var groups = definitions
            .GroupBy(d => NormalizeForMatching(d.Pattern).ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .ToList();
        
        foreach (var group in groups)
        {
            var defs = group.ToList();
            
            // Filter out alias definitions: same method name, same file, adjacent line numbers
            // These are intentional aliases like [When] and [Then] on the same method
            var filteredDefs = FilterOutAliasDefinitions(defs);
            
            // Only report as duplicate if there are still multiple definitions after filtering
            if (filteredDefs.Count > 1)
            {
                var projects = filteredDefs.Select(d => d.Project).Distinct().ToList();
                
                duplicates.Add(new DuplicateStepDefinitionGroup
                {
                    Pattern = filteredDefs.First().Pattern,
                    Definitions = filteredDefs,
                    IsCrossProject = projects.Count > 1,
                    Projects = projects
                });
            }
        }
        
        // Sort: cross-project first, then by number of duplicates
        return duplicates
            .OrderByDescending(d => d.IsCrossProject)
            .ThenByDescending(d => d.Definitions.Count)
            .ToList();
    }

    /// <summary>
    /// Filters out alias definitions - multiple attributes on the same method are intentional aliases.
    /// For example: [When("the task")] and [Then("the task")] on the same method.
    /// These should NOT be treated as duplicates.
    /// </summary>
    private List<StepDefinitionInfo> FilterOutAliasDefinitions(List<StepDefinitionInfo> definitions)
    {
        // Group by file + method name to find aliases
        var groupedByMethod = definitions
            .GroupBy(d => new { d.FilePath, d.MethodName })
            .ToList();
        
        var result = new List<StepDefinitionInfo>();
        
        foreach (var methodGroup in groupedByMethod)
        {
            var methodDefs = methodGroup.ToList();
            
            // If multiple definitions are on the same method (same file, same method name)
            // and their line numbers are close (within 5 lines), they are aliases
            if (methodDefs.Count > 1 && AreAliasDefinitions(methodDefs))
            {
                // Keep only one representative definition from the alias group
                // Prefer the one that matches the method name convention (Given > When > Then > And)
                var representative = methodDefs
                    .OrderBy(d => GetTypePreference(d.Type))
                    .First();
                result.Add(representative);
            }
            else
            {
                // Not aliases, include all definitions
                result.AddRange(methodDefs);
            }
        }
        
        return result;
    }

    /// <summary>
    /// Checks if a group of definitions on the same method are aliases (adjacent attributes).
    /// </summary>
    private bool AreAliasDefinitions(List<StepDefinitionInfo> definitions)
    {
        if (definitions.Count < 2) return false;
        
        // Check if all definitions are in the same file with close line numbers
        var lineNumbers = definitions.Select(d => d.LineNumber).OrderBy(l => l).ToList();
        var maxGap = lineNumbers.Zip(lineNumbers.Skip(1), (a, b) => b - a).Max();
        
        // If all attributes are within 5 lines of each other, they're stacked on the same method
        return maxGap <= 5;
    }

    /// <summary>
    /// Gets preference order for step types when selecting a representative alias.
    /// </summary>
    private int GetTypePreference(StepDefinitionType type)
    {
        return type switch
        {
            StepDefinitionType.Given => 0,
            StepDefinitionType.When => 1,
            StepDefinitionType.Then => 2,
            StepDefinitionType.And => 3,
            StepDefinitionType.But => 4,
            StepDefinitionType.StepDefinition => 5,
            _ => 99
        };
    }

    /// <summary>
    /// Calculates similarity between two strings using Levenshtein distance
    /// </summary>
    private double CalculateSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(target))
            return 1.0;
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            return 0.0;

        var distance = LevenshteinDistance(source, target);
        var maxLength = Math.Max(source.Length, target.Length);
        return 1.0 - ((double)distance / maxLength);
    }

    private int LevenshteinDistance(string source, string target)
    {
        var n = source.Length;
        var m = target.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    /// <summary>
    /// Fallback matching that normalizes both step and pattern by replacing quoted values with placeholders
    /// Example: "the user calls lookup for 'en-GB'" matches "the user calls lookup for '([^']*)'"
    /// </summary>
    private bool TryFallbackMatch(string stepText, string pattern)
    {
        try
        {
            // Normalize the step text: replace 'value' and "value" with placeholder
            var normalizedStep = NormalizeQuotedValues(stepText);
            
            // Normalize the pattern: replace regex groups with placeholder
            var normalizedPattern = NormalizePatternToText(pattern);
            
            // Compare normalized versions
            return string.Equals(normalizedStep, normalizedPattern, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces quoted values and Scenario Outline placeholders in step text with normalized placeholders
    /// Example: "the user calls lookup for 'en-GB'" -> "the user calls lookup for '<PARAM>'"
    /// Example: "user checks <Is Data> returned" -> "user checks <PARAM> returned"
    /// </summary>
    private string NormalizeQuotedValues(string text)
    {
        var result = text;
        
        // Replace Scenario Outline placeholders <...> with generic placeholder
        result = Regex.Replace(result, @"<[^>]+>", "<PARAM>");
        
        // Replace single-quoted values
        result = Regex.Replace(result, @"'[^']*'", "'<PARAM>'");
        
        // Replace double-quoted values
        result = Regex.Replace(result, @"""[^""]*""", "\"<PARAM>\"");
        
        // Replace standalone numbers that might be parameters (but not inside words)
        result = Regex.Replace(result, @"(?<!\w)\d+(?!\w)", "<NUM>");
        
        return result;
    }

    /// <summary>
    /// Normalizes a step definition pattern to comparable text
    /// Example: "the user calls lookup for '([^']*)'" -> "the user calls lookup for '<PARAM>'"
    /// Example: "^user checks (true|false) returned$" -> "user checks <PARAM> returned"
    /// </summary>
    private string NormalizePatternToText(string pattern)
    {
        var result = pattern;
        
        // Remove regex anchors
        result = result.TrimStart('^').TrimEnd('$');
        
        // Replace any regex capture group with <PARAM>
        // This handles: (true|false), ([^']*), (.+), (.*), (\d+), etc.
        result = Regex.Replace(result, @"\([^)]+\)", "<PARAM>");
        
        // Handle nested groups or escaped parens that might remain
        result = Regex.Replace(result, @"\\\(", "(");
        result = Regex.Replace(result, @"\\\)", ")");
        
        // Replace regex special characters with their literal equivalents
        result = result.Replace(@"\.", ".");
        result = result.Replace(@"\?", "?");
        result = result.Replace(@"\*", "*");
        result = result.Replace(@"\+", "+");
        result = result.Replace(@"\'", "'");
        result = result.Replace(@"\""", "\"");
        result = result.Replace(@"\\", "\\");
        result = result.Replace(@"\s", " ");
        result = result.Replace(@"\S", " ");
        
        // Handle SpecFlow placeholders {string}, {int} etc.
        result = Regex.Replace(result, @"\{string\}", "<PARAM>");
        result = Regex.Replace(result, @"\{int\}", "<NUM>");
        result = Regex.Replace(result, @"\{decimal\}", "<NUM>");
        result = Regex.Replace(result, @"\{float\}", "<NUM>");
        result = Regex.Replace(result, @"\{word\}", "<PARAM>");
        
        return result;
    }

    private string ExtractProjectName(string filePath)
    {
        var parts = filePath.Split(Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(filePath);
        
        while (!string.IsNullOrEmpty(directory))
        {
            var csprojFiles = Directory.GetFiles(directory, "*.csproj");
            if (csprojFiles.Any())
            {
                return Path.GetFileNameWithoutExtension(csprojFiles.First());
            }
            directory = Path.GetDirectoryName(directory);
        }

        return parts.Length >= 2 ? parts[^2] : "Unknown";
    }

    /// <summary>
    /// Helper class to track feature file steps during analysis
    /// </summary>
    private class FeatureStep
    {
        public string Project { get; set; } = string.Empty;
        public string FeatureFile { get; set; } = string.Empty;
        public string Scenario { get; set; } = string.Empty;
        public string StepText { get; set; } = string.Empty;
        public string FullStepText { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public bool IsMatched { get; set; }
        public StepDefinitionInfo? MatchedDefinition { get; set; }
        public bool IsFromIgnoredScenario { get; set; }

        /// <summary>
        /// The step's effective keyword, with And/But/* resolved to the nearest preceding concrete
        /// Given/When/Then in the scenario — Reqnroll binds on the resolved keyword, not the written
        /// one. Null when it could not be resolved (an And with no predecessor); such a step matches
        /// keyword-agnostically, so an unresolved keyword can never itself create a dead binding.
        /// </summary>
        public StepKeyword? ResolvedKeyword { get; set; }
    }
}
