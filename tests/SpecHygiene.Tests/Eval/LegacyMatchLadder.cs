using System.Text.RegularExpressions;
using SpecHygiene.Models;

namespace SpecHygiene.Tests.Eval;

/// <summary>
/// The eight-strategy match ladder EXACTLY as it stood before the Reqnroll port, lifted verbatim from
/// commit 9f55a75 so the eval compares against what actually shipped rather than a reconstruction.
/// Test-only baseline - nothing in production references it. Kept so the improvement is measurable,
/// and stays measurable if someone later questions a reclassified binding.
/// </summary>
public sealed class LegacyMatchLadder
{
    private static readonly string[] StepKeywords = { "Given ", "When ", "Then ", "And ", "But ", "* " };
    private readonly StepDefinitionParserShim _parser = new();

    /// <summary>The ladder called _parser.IsMatch as its Strategy 5. Reproduced as that same regex call.</summary>
    internal sealed class StepDefinitionParserShim
    {
        public bool IsMatch(string stepText, string regexPattern)
        {
            try { return Regex.IsMatch(stepText, regexPattern, RegexOptions.IgnoreCase); }
            catch { return false; }
        }
    }

    public bool TryMatchStepToDefinition(string stepText, StepDefinitionInfo definition)
    {
        var normalizedStep = NormalizeForMatching(stepText);
        var normalizedPattern = NormalizeForMatching(definition.Pattern);
        
        // Strategy 1: Exact text match (after normalization)
        if (string.Equals(normalizedStep, normalizedPattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        // Strategy 2: Try regex match with the step definition's regex pattern
        try
        {
            // Handle case-insensitive flag
            var regexPattern = definition.RegexPattern;
            var regexOptions = RegexOptions.IgnoreCase;
            
            if (regexPattern.Contains("(?i)"))
            {
                regexPattern = regexPattern.Replace("(?i)", "");
            }
            
            if (Regex.IsMatch(normalizedStep, regexPattern, regexOptions))
            {
                return true;
            }
        }
        catch { /* ignore regex errors */ }
        
        // Strategy 3: Try direct regex match if the pattern contains regex constructs like (.*)
        // This handles patterns like "user submits the estimate on the Order in (.*) (.*)"
        if (definition.Pattern.Contains("(.") || definition.Pattern.Contains("(["))
        {
            try
            {
                // Use the pattern directly as a regex (it already contains regex syntax)
                var directPattern = definition.Pattern.TrimStart('^').TrimEnd('$');
                if (directPattern.StartsWith("(?i)"))
                {
                    directPattern = directPattern.Substring(4);
                }
                
                // Add anchors for full match
                var fullPattern = "^" + directPattern + "$";
                if (Regex.IsMatch(normalizedStep, fullPattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
                
                // Also try without anchors for partial match
                if (Regex.IsMatch(normalizedStep, directPattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
            catch { /* ignore regex errors */ }
        }
        
        // Strategy 4: Match with placeholders replaced
        // "user checks <Is Data> returned" should match "user checks (true|false) returned"
        var stepWithPlaceholders = NormalizeWithPlaceholders(normalizedStep);
        var patternWithPlaceholders = NormalizeWithPlaceholders(normalizedPattern);
        
        
        if (string.Equals(stepWithPlaceholders, patternWithPlaceholders, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        
        // Strategy 5: Use the parser's IsMatch method
        try
        {
            if (_parser.IsMatch(normalizedStep, definition.RegexPattern))
            {
                return true;
            }
        }
        catch { /* ignore errors */ }
        
        // Strategy 6: Fuzzy text comparison for simple patterns
        // Handles minor whitespace differences
        var trimmedStep = Regex.Replace(normalizedStep, @"\s+", " ").Trim().ToLowerInvariant();
        var trimmedPattern = Regex.Replace(normalizedPattern, @"\s+", " ").Trim().ToLowerInvariant();
        
        // Also remove regex anchors and flags from pattern for text comparison
        trimmedPattern = trimmedPattern.TrimStart('^').TrimEnd('$');
        if (trimmedPattern.StartsWith("(?i)"))
        {
            trimmedPattern = trimmedPattern.Substring(4);
        }
        
        if (trimmedStep == trimmedPattern)
        {
            return true;
        }
        
        // Strategy 7: Handle patterns with alternations like (customer|primary)
        // Convert the pattern to text with placeholders and compare
        try
        {
            var patternAsText = ConvertPatternToTextWithPlaceholders(definition.Pattern);
            var stepAsText = ConvertStepToTextWithPlaceholders(normalizedStep);
            
            if (string.Equals(patternAsText, stepAsText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { /* ignore errors */ }
        
        // Strategy 8: Handle patterns with multiple (.*) capture groups
        // Pattern: "user submits the estimate on the Order in (.*) (.*)"
        // Step: "user submits the estimate on the Order in Motor Germany"
        if (definition.Pattern.Contains("(."))
        {
            try
            {
                // Replace all capture groups with a generic "WILDCARD" placeholder
                var patternWithWildcards = Regex.Replace(definition.Pattern, @"\([^)]+\)", "WILDCARD");
                
                // For the step, we need to identify where the wildcards would match
                // Split by "WILDCARD" to get the static parts
                var staticParts = patternWithWildcards.Split(new[] { "WILDCARD" }, StringSplitOptions.None);
                
                // Check if the step contains all the static parts in order
                var stepLower = normalizedStep.ToLowerInvariant();
                var currentIndex = 0;
                var allPartsFound = true;
                
                foreach (var part in staticParts)
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    
                    var partLower = part.ToLowerInvariant().Trim();
                    var foundIndex = stepLower.IndexOf(partLower, currentIndex, StringComparison.OrdinalIgnoreCase);
                    
                    if (foundIndex == -1)
                    {
                        allPartsFound = false;
                        break;
                    }
                    
                    currentIndex = foundIndex + partLower.Length;
                }
                
                if (allPartsFound)
                {
                    return true;
                }
            }
            catch { /* ignore errors */ }
        }
        
        return false;
    }

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

    private string NormalizeWithPlaceholders(string text)
    {
        var result = text;
        
        // Replace Scenario Outline placeholders: <Something> -> {P}
        result = Regex.Replace(result, @"<[^>]+>", "{P}");
        
        // Replace regex capture groups: (anything) -> {P}
        result = Regex.Replace(result, @"\([^)]+\)", "{P}");
        
        // Replace quoted strings: 'value' or "value" -> {P}
        result = Regex.Replace(result, @"'[^']*'", "{P}");
        result = Regex.Replace(result, @"""[^""]*""", "{P}");
        
        // Replace standalone numbers -> {P}
        result = Regex.Replace(result, @"(?<!\w)\d+(?!\w)", "{P}");
        
        return result;
    }

    private string ConvertPatternToTextWithPlaceholders(string pattern)
    {
        var result = pattern;
        
        // Remove anchors and flags
        result = result.TrimStart('^').TrimEnd('$');
        if (result.StartsWith("(?i)"))
        {
            result = result.Substring(4);
        }
        
        // Replace custom type placeholders like {FormConfiguratorArea} with PARAM
        result = Regex.Replace(result, @"\{[A-Za-z_][A-Za-z0-9_]*\}", "PARAM");
        
        // Replace regex groups with PARAM placeholder
        // Handle nested groups by replacing from inside out
        while (Regex.IsMatch(result, @"\([^()]+\)"))
        {
            result = Regex.Replace(result, @"\([^()]+\)", "PARAM");
        }
        
        // Replace escaped characters (like \( \) \. become literal ( ) . )
        result = Regex.Replace(result, @"\\(.)", "$1");
        
        // Normalize whitespace
        result = Regex.Replace(result, @"\s+", " ").Trim().ToLowerInvariant();
        
        return result;
    }

    private string ConvertStepToTextWithPlaceholders(string step)
    {
        var result = step;
        
        // Replace quoted strings with PARAM (both single and double quotes)
        result = Regex.Replace(result, @"'[^']*'", "PARAM");
        result = Regex.Replace(result, @"""[^""]*""", "PARAM");
        
        // Replace Scenario Outline placeholders with PARAM
        result = Regex.Replace(result, @"<[^>]+>", "PARAM");
        
        // Replace standalone numbers with PARAM
        result = Regex.Replace(result, @"\b\d+\b", "PARAM");
        
        // Replace words that look like enum values (PascalCase or UPPERCASE) - common in custom type matches
        // This helps match "FrontOffice" against {FormConfiguratorArea}
        // But be careful not to replace too much - only standalone PascalCase words
        
        // Normalize whitespace
        result = Regex.Replace(result, @"\s+", " ").Trim().ToLowerInvariant();
        
        return result;
    }

    private bool IsRegexPattern(string pattern)
    {
        return pattern.StartsWith("^") || 
               pattern.EndsWith("$") || 
               pattern.Contains("(?") ||
               pattern.Contains("(.") ||
               pattern.Contains("(\\d") ||
               pattern.Contains("([^") ||
               (pattern.Contains("|") && pattern.Contains("("));
    }
}
