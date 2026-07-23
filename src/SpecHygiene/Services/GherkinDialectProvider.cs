using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpecHygiene.Models;

namespace SpecHygiene.Services;

/// <summary>
/// Loads Gherkin keyword dialects from the embedded canonical <c>gherkin-languages.json</c> and
/// detects a feature file's dialect from its <c># language:</c> header. Absent a header, the file is
/// treated as English, so existing behavior is unchanged.
///
/// One shared, immutable instance is enough for a whole run — use <see cref="Default"/>.
/// </summary>
public sealed class GherkinDialectProvider
{
    public const string DefaultLanguage = "en";

    // Gherkin's language header: the first significant line may be "# language: xx".
    private static readonly Regex LanguageHeaderRegex =
        new(@"^#\s*language\s*:\s*([A-Za-z0-9_-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Dictionary<string, GherkinDialect> _dialects;

    public static GherkinDialectProvider Default { get; } = new();

    public GherkinDialectProvider()
    {
        _dialects = LoadEmbedded();
    }

    private static Dictionary<string, GherkinDialect> LoadEmbedded()
    {
        var assembly = typeof(GherkinDialectProvider).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("gherkin-languages.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded gherkin-languages.json resource not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var doc = JsonDocument.Parse(stream);

        var dialects = new Dictionary<string, GherkinDialect>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in doc.RootElement.EnumerateObject())
        {
            var e = lang.Value;
            dialects[lang.Name] = new GherkinDialect(
                lang.Name,
                Array(e, "feature"),
                Array(e, "background"),
                Array(e, "scenario"),
                Array(e, "scenarioOutline"),
                Array(e, "examples"),
                Array(e, "rule"),
                StepKeywords(e));
        }
        return dialects;
    }

    private static string[] Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetString()!).Where(s => s is not null).ToArray()
            : System.Array.Empty<string>();

    // Given/When/Then/And/But collapsed into one distinct set, trimmed of the trailing space the
    // source carries ("Given " -> "Given", "* " -> "*").
    private static string[] StepKeywords(JsonElement element)
    {
        var result = new List<string>();
        foreach (var concept in new[] { "given", "when", "then", "and", "but" })
        {
            foreach (var kw in Array(element, concept))
            {
                var trimmed = kw.TrimEnd();
                if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    result.Add(trimmed);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Detects the dialect from the file's <c># language:</c> header. Scans leading blank/comment
    /// lines; the first <c># language: xx</c> wins. If there is no header (or the code is unknown),
    /// falls back to <paramref name="fallbackLanguage"/> (default English) so existing files are
    /// unaffected.
    /// </summary>
    public GherkinDialect Detect(IEnumerable<string> lines, string? fallbackLanguage = null)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var match = LanguageHeaderRegex.Match(line);
            if (match.Success)
                return GetDialect(match.Groups[1].Value)
                    ?? GetDialect(fallbackLanguage ?? DefaultLanguage)!;

            if (line.StartsWith("#"))
                continue; // some other comment before the header — keep looking

            break; // first real content line: past the header zone
        }

        return GetDialect(fallbackLanguage ?? DefaultLanguage)!;
    }

    /// <summary>Returns the dialect for a language code, or null if unknown. Never null for "en".</summary>
    public GherkinDialect? GetDialect(string language) =>
        _dialects.TryGetValue(language, out var dialect) ? dialect : null;
}
