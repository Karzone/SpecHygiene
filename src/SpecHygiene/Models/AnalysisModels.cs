using System;

namespace SpecHygiene.Models;

public record StepWithData(
    string StepText,
    List<Dictionary<string, string>> DataTable,
    string DataFingerprint
);

public record StepOccurrence(
    string Project,
    string FeatureFile,
    string FeatureFilePath,
    string Scenario,
    StepWithData Step,
    int LineNumber,
    string OriginalStepText = ""  // NEW: Store original step text with keyword
);

public record DuplicateGroup(
    string StepPattern,
    string DataFingerprint,
    List<Dictionary<string, string>> SampleData,
    List<StepOccurrence> Occurrences,
    DuplicateCategory Category
);

public record StepVariationsGroup(
    string StepText,
    List<DataVariation> Variations
);

public record DataVariation(
    List<Dictionary<string, string>> SampleData,
    List<StepOccurrence> Occurrences
);

public record DataError(
    string Project,
    string FeatureFile,
    string FeatureFilePath,
    string Scenario,
    string StepText,
    int LineNumber,
    List<string> UndefinedPlaceholders,
    List<string> DefinedPlaceholders,
    DataErrorType ErrorType,
    bool HasExamplesSection
);

// Enhanced scenario-related models
public record ScenarioInfo(
    string Project,
    string FeatureFile,
    string FeatureFilePath,
    string ScenarioName,
    int ScenarioLineNumber,
    List<string> StepFingerprints,
    List<string> StepTexts,
    string ScenarioFingerprint
)
{
    // Optional enhanced properties with defaults
    public List<string> NormalizedStepTexts { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public List<string> BackgroundSteps { get; init; } = new();
    public List<string> BackgroundFingerprints { get; init; } = new();

    /// <summary>
    /// "Scenario" or "ScenarioOutline". Computed during parsing (previously discarded); stored for the
    /// inventory exporter. Display/inventory only — not used by detection.
    /// </summary>
    public string ScenarioType { get; init; } = "Scenario";

    /// <summary>
    /// Original step text WITH the Gherkin keyword (Given/When/Then/And/But), in lockstep with
    /// StepFingerprints/StepTexts. Display-only — used by the HTML report's Gherkin code view so it
    /// can show and colour keywords. Never used for detection/comparison. May be empty for scenarios
    /// produced before this field existed; the reporter falls back to StepTexts when so.
    /// </summary>
    public List<string> OriginalStepTexts { get; init; } = new();

    /// <summary>
    /// Per-step data-table rows, in lockstep with StepTexts (an empty inner list = that step has no
    /// table). The fingerprint keys on these cells, so the AI merge prompt must see them too — else it
    /// judges a merge on LESS information than the detector used (two scenarios that differ only in a
    /// table cell look identical to a text-only prompt). May be empty for scenarios produced before
    /// this field existed; consumers fall back to showing step text alone when so.
    /// </summary>
    public List<List<Dictionary<string, string>>> StepDataTables { get; init; } = new();
}

public record ScenarioMatch(
    ScenarioInfo Scenario,
    double OverlapPercentage,
    int MatchingSteps,
    int TotalStepsBase,
    int TotalStepsMatch,
    bool SameSequence,
    List<string> MatchingStepTexts,
    List<string> UniqueToBase,
    List<string> UniqueToMatch
)
{
    // Optional enhanced properties with defaults
    public MatchType MatchType { get; init; } = MatchType.Exact;
    public double FuzzyScore { get; init; } = 0;
    public int PotentialStepReuse { get; init; } = 0;
    public List<FuzzyStepMatch> FuzzyMatches { get; init; } = new();
}

public enum MatchType
{
    Exact,
    ParameterVariation,
    FuzzyMatch,
    Mixed
}

public record FuzzyStepMatch(
    string BaseStep,
    string MatchStep,
    double Similarity
);

public record ScenarioDuplicateGroup(
    ScenarioInfo BaseScenario,
    List<ScenarioMatch> Matches,
    ScenarioDuplicateType DuplicateType
)
{
    // Optional enhanced properties with defaults
    public int StepReuseScore { get; init; } = 0;
    public bool SuggestScenarioOutline { get; init; } = false;
    public List<string> CommonBackgroundSteps { get; init; } = new();
}

public enum DuplicateCategory
{
    ExactDuplicate,
    SameStepDifferentData,
    SimilarStepSameData,
    SetupStep
}

public enum ScenarioDuplicateType
{
    Exact,          // 100% match
    Superset,       // Match contains all of base + more
    Subset,         // Base contains all of match + more  
    HighOverlap,    // Above threshold but not exact/superset/subset
    ParameterVariation,  // NEW
    FuzzyMatch           // NEW
}

public enum DataErrorType
{
    UndefinedPlaceholder,
    ScenarioWithExamples,
    DuplicateColumnHeader,
    MismatchedColumnCount,
    DataSourceNotFound  // NEW in Phase 0
}

/// <summary>
/// Represents a single step in a scenario for display purposes
/// </summary>
public class ScenarioStepInfo
{
    public string Keyword { get; set; } = string.Empty;  // Given, When, Then, And, But
    public string Text { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public bool IsRedundantStep { get; set; }
    
    /// <summary>
    /// Data table associated with this step (if any)
    /// </summary>
    public List<Dictionary<string, string>>? DataTable { get; set; }
}

/// <summary>
/// Represents Examples table data for a Scenario Outline
/// </summary>
public class ScenarioExamplesData
{
    public List<string> Headers { get; set; } = new();
    public List<Dictionary<string, string>> Rows { get; set; } = new();
}

// ======== Step Definition Coverage Models ========

/// <summary>
/// Type of step definition attribute
/// </summary>
public enum StepDefinitionType
{
    Given,
    When,
    Then,
    And,
    But,
    StepDefinition  // Generic [StepDefinition] attribute
}

/// <summary>
/// Represents a step definition found in code
/// </summary>
public class StepDefinitionInfo
{
    public string MethodName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string RegexPattern { get; set; } = string.Empty;
    public StepDefinitionType Type { get; set; }
    public int LineNumber { get; set; }
    public int UsageCount { get; set; }
    public List<StepUsage> Usages { get; set; } = new();

    /// <summary>
    /// Number of method parameters. Only load-bearing for a bare [Given] with no pattern string
    /// (method-name convention, R9): Reqnroll binds by the PascalCase name only when the method takes
    /// NO parameters — a parameterised method under bare convention never binds at all.
    /// </summary>
    public int ParameterCount { get; set; }

    /// <summary>
    /// True when the step attribute HAS a pattern argument but it is not a plain string literal the
    /// syntactic pass can read — e.g. an interpolated string <c>$@"…{Const}…"</c>, a const reference,
    /// or a concatenation. Such a binding is NOT a method-name-convention binding (that is a bare
    /// attribute with NO argument), and its real pattern is unknown to us, so it must be held out of
    /// the unused list as indeterminate rather than mis-read as an empty convention pattern.
    /// </summary>
    public bool UnresolvablePattern { get; set; }

    /// <summary>
    /// The project containing this step definition
    /// </summary>
    public string Project { get; set; } = string.Empty;
}

/// <summary>
/// Represents a usage of a step definition in a feature file
/// </summary>
public class StepUsage
{
    public string FeatureFile { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string StepText { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string Project { get; set; } = string.Empty;
}

/// <summary>
/// Represents a scenario that has been marked as ignored/skipped
/// </summary>
public class IgnoredScenario
{
    public string Project { get; set; } = string.Empty;
    public string FeatureFile { get; set; } = string.Empty;
    public string FeatureFilePath { get; set; } = string.Empty;
    public string ScenarioName { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public List<string> Tags { get; set; } = new();
    public string IgnoreReason { get; set; } = string.Empty;  // The tag that caused it to be ignored
}

/// <summary>
/// Overall coverage analysis results
/// </summary>
public class StepDefinitionCoverageReport
{
    public int TotalStepDefinitions { get; set; }
    public int UsedStepDefinitions { get; set; }
    public int UnusedStepDefinitions { get; set; }
    public double CoveragePercentage { get; set; }
    
    /// <summary>
    /// Step definitions that are never used
    /// </summary>
    public List<StepDefinitionInfo> UnusedDefinitions { get; set; } = new();
    
    /// <summary>
    /// Step definitions sorted by usage count (most used first)
    /// </summary>
    public List<StepDefinitionInfo> MostUsedDefinitions { get; set; } = new();
    
    /// <summary>
    /// Step definitions used only once (candidates for inline)
    /// </summary>
    public List<StepDefinitionInfo> SingleUseDefinitions { get; set; } = new();
    
    /// <summary>
    /// Scenarios that are ignored/skipped
    /// </summary>
    public List<IgnoredScenario> IgnoredScenarios { get; set; } = new();
    
    /// <summary>
    /// Step definitions with the same pattern defined in multiple places (duplicate definitions)
    /// </summary>
    public List<DuplicateStepDefinitionGroup> DuplicateDefinitions { get; set; } = new();

    /// <summary>
    /// Definitions whose pattern could not be evaluated, so we cannot say whether any step binds to
    /// them. Deliberately NOT in <see cref="UnusedDefinitions"/> — "could not tell" is not "unused",
    /// and reporting them as dead would invite deleting live code. Surfaced so the three buckets
    /// reconcile: used + unused + indeterminate == total.
    /// </summary>
    public List<IndeterminateStepDefinition> IndeterminateDefinitions { get; set; } = new();

    /// <summary>
    /// Unused definitions listed in the known-issues CSV — genuinely dead, but already triaged and
    /// accepted (a step whose only usage is commented out, a binding kept for a reason tracked
    /// elsewhere). Still counted as unused; separated so a run's ACTIONABLE list is the fresh
    /// findings rather than the same accepted ones re-litigated every time.
    /// </summary>
    public List<KnownIssueStepDefinition> KnownIssueDefinitions { get; set; } = new();

    /// <summary>Unused definitions that are NOT in the known-issues CSV — the actionable list.</summary>
    public List<StepDefinitionInfo> ActionableUnusedDefinitions { get; set; } = new();

    /// <summary>
    /// Private helper methods that become unreachable ONCE the unused bindings above are deleted —
    /// the dead-binding → dead-code cascade. Candidates only, never auto-delete: they inherit the
    /// unused list's assumptions plus the reflection/DI blind spot. Empty unless there are dead bindings.
    /// </summary>
    public List<Analysis.Reqnroll.CascadeOrphan> CascadeOrphans { get; set; } = new();
}

/// <summary>An unused definition that the known-issues CSV accounts for, with its comment.</summary>
public class KnownIssueStepDefinition
{
    public StepDefinitionInfo Definition { get; set; } = new();
    public string Comment { get; set; } = string.Empty;
}

/// <summary>A step definition that could not be evaluated this run, and why.</summary>
public class IndeterminateStepDefinition
{
    public StepDefinitionInfo Definition { get; set; } = new();

    /// <summary>e.g. unresolved {CustomType}, invalid regex, method-name convention.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Represents a group of step definitions with the same pattern defined in multiple places
/// </summary>
public class DuplicateStepDefinitionGroup
{
    /// <summary>
    /// The normalized pattern that is duplicated
    /// </summary>
    public string Pattern { get; set; } = string.Empty;
    
    /// <summary>
    /// All step definitions with this pattern
    /// </summary>
    public List<StepDefinitionInfo> Definitions { get; set; } = new();
    
    /// <summary>
    /// Whether this is a cross-project duplicate
    /// </summary>
    public bool IsCrossProject { get; set; }
    
    /// <summary>
    /// Projects where this pattern is defined
    /// </summary>
    public List<string> Projects { get; set; } = new();
}

public class DuplicateAnalysisReport
{
    // Existing properties
    public List<DuplicateGroup> CrossProjectDuplicates { get; set; } = new();
    public List<DuplicateGroup> WithinProjectDuplicates { get; set; } = new();
    public List<DuplicateGroup> SetupStepDuplicates { get; set; } = new();
    public List<StepVariationsGroup> SameStepDifferentData { get; set; } = new();
    public List<DataError> DataErrors { get; set; } = new();

    // Scenario duplicates
    public List<ScenarioDuplicateGroup> ScenarioDuplicates { get; set; } = new();
    
    
    /// <summary>
    /// Step definition coverage analysis results
    /// </summary>
    public StepDefinitionCoverageReport? StepDefinitionCoverage { get; set; }
    
    // NEW: Enhanced statistics
    public int TotalStepsAnalyzed { get; set; }
    public int TotalScenariosAnalyzed { get; set; }
    public int ScenariosExcludedByTags { get; set; }
    public int FeatureFilesScanned { get; set; }
    public int ProjectsScanned { get; set; }
    public int FuzzyMatchesFound { get; set; }
    public int ParameterVariationsFound { get; set; }
    public int PotentialStepReuse { get; set; }
    public int ScenarioOutlineCandidates { get; set; }
    public DuplicateMode AnalysisMode { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Unused code analysis results (dead methods, classes, interfaces)
    /// </summary>
    public UnusedCodeReport? UnusedCodeReport { get; set; }

    /// <summary>
    /// Every scenario seen during the parse pass, in parse order. Populated regardless of
    /// duplicate-matching mode (including parse-only mode) so the scenario-inventory exporter can
    /// run without paying for cross-scenario matching. Not used by detection.
    /// </summary>
    public List<ScenarioInfo> AllScenarios { get; set; } = new();

    /// <summary>
    /// Feature files that failed to parse during the pass. Recorded rather than silently dropped so
    /// a parse failure is visible in the inventory artefacts instead of a scenario quietly vanishing.
    /// </summary>
    public List<ParseFailure> ParseFailures { get; set; } = new();

}

/// <summary>
/// A feature file that could not be parsed, with the reason. Surfaced in the inventory so failures
/// are never silent (a parse failure must not remove a feature from the inventory without trace).
/// </summary>
public record ParseFailure(string Project, string FeatureFilePath, string Reason);

// ======== Unused Code Analysis Models ========

/// <summary>
/// Type of code element (method, class, interface)
/// </summary>
public enum CodeElementType
{
    Method,
    Class,
    Interface
}

/// <summary>
/// Represents an unused code element (method, class, or interface)
/// </summary>
public class UnusedCodeInfo
{
    /// <summary>
    /// Name of the element (method name, class name, interface name)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type of code element
    /// </summary>
    public CodeElementType ElementType { get; set; }

    /// <summary>
    /// Containing class (for methods) or namespace
    /// </summary>
    public string ContainingType { get; set; } = string.Empty;

    /// <summary>
    /// Full file path
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Line number where the element is declared
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Project containing this element
    /// </summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// Access modifier (public, private, internal, protected)
    /// </summary>
    public string Visibility { get; set; } = string.Empty;

    /// <summary>
    /// Method signature or class declaration snippet
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a static member
    /// </summary>
    public bool IsStatic { get; set; }

    /// <summary>
    /// Reason why this was flagged as unused
    /// </summary>
    public string Reason { get; set; } = "No references found";

    /// <summary>
    /// This element's name also appears as a raw string literal somewhere in the scanned source —
    /// the one way a symbol can be invoked without a resolvable reference (reflection:
    /// GetMethod("Name")/Type.GetType, a DI key, a serialized member). nameof is NOT this — Roslyn
    /// resolves nameof to the symbol, so it already counts as a real reference. Non-null means "rule
    /// out reflection before deleting"; it does not overturn the unused verdict, it qualifies it.
    /// </summary>
    public string? StringLiteralHint { get; set; }
}

/// <summary>
/// Report containing all unused code analysis results
/// </summary>
public class UnusedCodeReport
{
    /// <summary>
    /// Methods with 0 references (excluding step definitions and hooks)
    /// </summary>
    public List<UnusedCodeInfo> UnusedMethods { get; set; } = new();

    /// <summary>
    /// Classes with 0 references (not instantiated or used statically)
    /// </summary>
    public List<UnusedCodeInfo> UnusedClasses { get; set; } = new();

    /// <summary>
    /// Interfaces with 0 implementations or references
    /// </summary>
    public List<UnusedCodeInfo> UnusedInterfaces { get; set; } = new();

    // Statistics
    public int TotalMethodsScanned { get; set; }
    public int TotalClassesScanned { get; set; }
    public int TotalInterfacesScanned { get; set; }
    public int TotalFilesScanned { get; set; }

    /// <summary>
    /// The roots actually scanned. This analyzer is path-based, not solution-aware: it reads every
    /// .cs under these roots into one compilation and knows nothing of project boundaries or a .sln.
    /// A reference outside these roots is invisible — which is why a public member consumed only by an
    /// unscanned project would read as unused. Surfaced so a scoped result is never mistaken for a
    /// solution-wide one.
    /// </summary>
    public List<string> ScannedRoots { get; set; } = new();

    /// <summary>Unused findings whose name also appears as a string literal (possible reflection).</summary>
    public int StringLiteralHintCount =>
        UnusedMethods.Count(m => m.StringLiteralHint != null)
        + UnusedClasses.Count(c => c.StringLiteralHint != null)
        + UnusedInterfaces.Count(i => i.StringLiteralHint != null);

    /// <summary>
    /// Percentage of methods that are unused
    /// </summary>
    public double UnusedMethodPercentage => TotalMethodsScanned > 0 
        ? (UnusedMethods.Count * 100.0 / TotalMethodsScanned) 
        : 0;

    /// <summary>
    /// Analysis duration in milliseconds
    /// </summary>
    public long AnalysisDurationMs { get; set; }
}