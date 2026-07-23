namespace SpecHygiene.Models;

/// <summary>
/// The set of Gherkin keywords for one language dialect (e.g. <c>en</c>, <c>de</c>). Sourced from the
/// canonical <c>gherkin-languages.json</c> that Gherkin/Reqnroll ship, so a localized feature file
/// (<c># language: de</c> with <c>Szenario:</c>, <c>Beispiele:</c>) is recognized exactly like its
/// English equivalent.
///
/// Block keywords (Feature/Background/Scenario/Examples) are matched as <c>keyword + ":"</c>;
/// step keywords (Given/When/Then/And/But and their aliases) are stored trimmed and matched as
/// <c>keyword + " "</c>. A dialect can have several keywords per concept (German scenario =
/// <c>Szenario</c> or <c>Beispiel</c>), which is why every concept is a list.
/// </summary>
public sealed class GherkinDialect
{
    public string Language { get; }
    public IReadOnlyList<string> Feature { get; }
    public IReadOnlyList<string> Background { get; }
    public IReadOnlyList<string> Scenario { get; }
    public IReadOnlyList<string> ScenarioOutline { get; }
    public IReadOnlyList<string> Examples { get; }
    public IReadOnlyList<string> Rule { get; }

    /// <summary>Given/When/Then/And/But keywords (and aliases), trimmed of the trailing space the
    /// source data carries — e.g. <c>"Given "</c> → <c>"Given"</c>, <c>"* "</c> → <c>"*"</c>.</summary>
    public IReadOnlyList<string> StepKeywords { get; }

    public GherkinDialect(
        string language,
        IReadOnlyList<string> feature,
        IReadOnlyList<string> background,
        IReadOnlyList<string> scenario,
        IReadOnlyList<string> scenarioOutline,
        IReadOnlyList<string> examples,
        IReadOnlyList<string> rule,
        IReadOnlyList<string> stepKeywords)
    {
        Language = language;
        Feature = feature;
        Background = background;
        Scenario = scenario;
        ScenarioOutline = scenarioOutline;
        Examples = examples;
        Rule = rule;
        StepKeywords = stepKeywords;
    }

    public bool IsFeature(string line) => MatchesBlock(line, Feature);
    public bool IsBackground(string line) => MatchesBlock(line, Background);
    public bool IsScenario(string line) => MatchesBlock(line, Scenario);
    public bool IsScenarioOutline(string line) => MatchesBlock(line, ScenarioOutline);
    public bool IsExamples(string line) => MatchesBlock(line, Examples);

    /// <summary>True for a plain scenario OR a scenario outline/template start line.</summary>
    public bool IsScenarioStart(string line) => IsScenarioOutline(line) || IsScenario(line);

    /// <summary>A block keyword must be immediately followed by a colon (<c>Scenario:</c>), matching
    /// the historical literal checks. Outline keywords are checked before plain scenario keywords by
    /// callers so <c>Scenario Outline:</c> is never mistaken for <c>Scenario:</c>.</summary>
    private static bool MatchesBlock(string line, IReadOnlyList<string> keywords)
    {
        foreach (var kw in keywords)
        {
            if (line.Length > kw.Length &&
                line[kw.Length] == ':' &&
                line.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True if the line begins with a step keyword followed by a space. Pass
    /// <paramref name="keywords"/> to override (e.g. user-configured English step keywords).</summary>
    public bool IsStep(string line, IReadOnlyList<string>? keywords = null)
    {
        foreach (var kw in keywords ?? StepKeywords)
        {
            if (line.StartsWith(kw + " ", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Removes a leading step keyword from a step, returning the trimmed remainder.</summary>
    public string StripStepKeyword(string stepText, IReadOnlyList<string>? keywords = null)
    {
        foreach (var kw in keywords ?? StepKeywords)
        {
            if (stepText.StartsWith(kw + " ", StringComparison.OrdinalIgnoreCase))
                return stepText.Substring(kw.Length).Trim();
        }
        return stepText.Trim();
    }
}
