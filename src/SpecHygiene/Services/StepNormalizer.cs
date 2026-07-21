// Services/StepNormalizer.cs
using System.Text.RegularExpressions;

namespace SpecHygiene.Services;

public class StepNormalizer
{
    private static readonly Regex PlaceholderRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex QuotedStringRegex = new(@"""[^""]*""", RegexOptions.Compiled);
    private static readonly Regex SingleQuotedStringRegex = new(@"'[^']*'", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\b\d+\.?\d*\b", RegexOptions.Compiled);
    private static readonly Regex KeywordRegex = new(@"^(Given|When|Then|And|But)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtraWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ArticlesRegex = new(@"\b(a|an|the)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Normalize step for keyword-agnostic comparison (removes Given/When/Then/And/But)
    /// </summary>
    public string NormalizeKeyword(string stepText)
    {
        return KeywordRegex.Replace(stepText, "").Trim();
    }

    /// <summary>
    /// Normalize step for parameter-agnostic comparison (replaces placeholders, quoted strings, numbers with tokens)
    /// </summary>
    public string NormalizeParameters(string stepText)
    {
        var normalized = stepText;
        
        // Replace <Placeholder> with {PARAM}
        normalized = PlaceholderRegex.Replace(normalized, "{PARAM}");
        
        // Replace "quoted strings" with {STRING}
        normalized = QuotedStringRegex.Replace(normalized, "{STRING}");
        normalized = SingleQuotedStringRegex.Replace(normalized, "{STRING}");
        
        // Replace numbers with {NUM}
        normalized = NumberRegex.Replace(normalized, "{NUM}");
        
        return normalized;
    }

    /// <summary>
    /// Normalize for fuzzy matching (removes articles, extra whitespace, lowercase)
    /// </summary>
    public string NormalizeForFuzzy(string stepText)
    {
        var normalized = NormalizeKeyword(stepText);
        normalized = NormalizeParameters(normalized);
        
        // Remove articles (a, an, the)
        normalized = ArticlesRegex.Replace(normalized, "");
        
        // Normalize whitespace
        normalized = ExtraWhitespaceRegex.Replace(normalized, " ").Trim();
        
        return normalized.ToLowerInvariant();
    }

    /// <summary>
    /// Full normalization for duplicate detection
    /// </summary>
    public string FullNormalize(string stepText)
    {
        return NormalizeForFuzzy(stepText);
    }

    /// <summary>
    /// Calculate Levenshtein distance between two strings
    /// </summary>
    public int LevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source))
            return string.IsNullOrEmpty(target) ? 0 : target.Length;

        if (string.IsNullOrEmpty(target))
            return source.Length;

        var sourceLength = source.Length;
        var targetLength = target.Length;

        var distance = new int[sourceLength + 1, targetLength + 1];

        for (var i = 0; i <= sourceLength; i++)
            distance[i, 0] = i;

        for (var j = 0; j <= targetLength; j++)
            distance[0, j] = j;

        for (var i = 1; i <= sourceLength; i++)
        {
            for (var j = 1; j <= targetLength; j++)
            {
                var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[sourceLength, targetLength];
    }

    /// <summary>
    /// Calculate similarity percentage between two strings (0-100)
    /// </summary>
    public double CalculateSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(target))
            return 100;

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            return 0;

        var normalizedSource = NormalizeForFuzzy(source);
        var normalizedTarget = NormalizeForFuzzy(target);

        if (normalizedSource == normalizedTarget)
            return 100;

        var distance = LevenshteinDistance(normalizedSource, normalizedTarget);
        var maxLength = Math.Max(normalizedSource.Length, normalizedTarget.Length);

        return Math.Round((1 - (double)distance / maxLength) * 100, 1);
    }

    /// <summary>
    /// Check if two steps are fuzzy matches based on threshold
    /// </summary>
    public bool IsFuzzyMatch(string step1, string step2, double threshold = 85)
    {
        return CalculateSimilarity(step1, step2) >= threshold;
    }
}
