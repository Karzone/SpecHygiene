using System.Text;
using System.Text.RegularExpressions;
using SpecHygiene.Models;

namespace SpecHygiene.Analysis;

/// <summary>
/// Parses C# files to extract step definitions (SpecFlow/Reqnroll/Cucumber)
/// </summary>
public class StepDefinitionParser
{
    // Patterns to match step definition attributes
    private static readonly Regex GivenAttributeRegex = new(
        @"\[Given\s*\(\s*@?""(.+?)""\s*\)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);
    
    private static readonly Regex WhenAttributeRegex = new(
        @"\[When\s*\(\s*@?""(.+?)""\s*\)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);
    
    private static readonly Regex ThenAttributeRegex = new(
        @"\[Then\s*\(\s*@?""(.+?)""\s*\)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);
    
    private static readonly Regex AndAttributeRegex = new(
        @"\[And\s*\(\s*@?""(.+?)""\s*\)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);
    
    private static readonly Regex ButAttributeRegex = new(
        @"\[But\s*\(\s*@?""(.+?)""\s*\)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);
    
    private static readonly Regex StepDefinitionAttributeRegex = new(
        @"\[StepDefinition\s*\(\s*@?""(.+?)""\s*\)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);
    
    
    // Pattern to match method declaration after attributes
    // Supports: optional access modifiers, static, async, virtual, override, return types, generic types
    private static readonly Regex MethodDeclarationRegex = new(
        @"(?:(?:public|private|protected|internal)\s+)?(?:static\s+)?(?:async\s+)?(?:virtual\s+)?(?:override\s+)?(?:\w+(?:<[^>]+>)?(?:\?)?)\s+(\w+)\s*\(",
        RegexOptions.Compiled);
    
    // Pattern to extract class name
    private static readonly Regex ClassDeclarationRegex = new(
        @"(?:public|private|protected|internal)?\s*(?:partial\s+)?class\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses all C# files in the given directories to find step definitions
    /// </summary>
    public List<StepDefinitionInfo> ParseStepDefinitions(IEnumerable<string> directories)
    {
        var stepDefinitions = new List<StepDefinitionInfo>();

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            var csFiles = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));

            foreach (var filePath in csFiles)
            {
                try
                {
                    var definitions = ParseFile(filePath);
                    stepDefinitions.AddRange(definitions);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to parse {filePath}: {ex.Message}");
                }
            }
        }

        return stepDefinitions;
    }

    /// <summary>
    /// Parses a single C# file for step definitions
    /// </summary>
    public List<StepDefinitionInfo> ParseFile(string filePath)
    {
        var definitions = new List<StepDefinitionInfo>();
        var content = File.ReadAllText(filePath);
        var lines = content.Split('\n');
        
        // Extract project name from path
        var projectName = ExtractProjectName(filePath);
        
        // Find class name
        var className = ExtractClassName(content);

        // Find all step definition attributes with their line numbers
        var attributeMatches = new List<(Match Match, StepDefinitionType Type, int LineNumber)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1;

            CheckAndAddMatch(GivenAttributeRegex, line, StepDefinitionType.Given, lineNumber, attributeMatches);
            CheckAndAddMatch(WhenAttributeRegex, line, StepDefinitionType.When, lineNumber, attributeMatches);
            CheckAndAddMatch(ThenAttributeRegex, line, StepDefinitionType.Then, lineNumber, attributeMatches);
            CheckAndAddMatch(AndAttributeRegex, line, StepDefinitionType.And, lineNumber, attributeMatches);
            CheckAndAddMatch(ButAttributeRegex, line, StepDefinitionType.But, lineNumber, attributeMatches);
            CheckAndAddMatch(StepDefinitionAttributeRegex, line, StepDefinitionType.StepDefinition, lineNumber, attributeMatches);
        }

        // For each attribute, find the associated method
        foreach (var (match, type, lineNumber) in attributeMatches)
        {
            var pattern = match.Groups[1].Value;
            var methodName = FindMethodName(lines, lineNumber - 1); // Convert to 0-based index

            var definition = new StepDefinitionInfo
            {
                MethodName = methodName ?? "Unknown",
                ClassName = className ?? "Unknown",
                FilePath = filePath,
                Pattern = pattern,
                RegexPattern = ConvertToRegex(pattern),
                Type = type,
                LineNumber = lineNumber,
                Project = projectName
            };

            definitions.Add(definition);
        }

        return definitions;
    }

    private void CheckAndAddMatch(
        Regex regex,
        string line,
        StepDefinitionType type,
        int lineNumber,
        List<(Match, StepDefinitionType, int)> matches)
    {
        var match = regex.Match(line);
        if (match.Success)
        {
            matches.Add((match, type, lineNumber));
        }
    }

    private string? ExtractClassName(string content)
    {
        var match = ClassDeclarationRegex.Match(content);
        return match.Success ? match.Groups[1].Value : null;
    }

    private string? FindMethodName(string[] lines, int attributeLineIndex)
    {
        // Look for method declaration in the lines following the attribute
        // Skip over additional attributes that might be stacked on the method
        for (int i = attributeLineIndex + 1; i < Math.Min(attributeLineIndex + 15, lines.Length); i++)
        {
            var trimmedLine = lines[i].TrimStart();
            
            // Skip empty lines and additional attributes (but not class-level ones)
            if (string.IsNullOrWhiteSpace(trimmedLine))
                continue;
                
            // If it's another attribute, continue looking (could be stacked attributes like [Scope], [Binding], etc.)
            if (trimmedLine.StartsWith("[") && !trimmedLine.Contains("class"))
                continue;
            
            // Stop if we hit a class declaration
            if (trimmedLine.Contains("class ") && !trimmedLine.Contains("\"class"))
                break;
            
            // Try to match method declaration
            var match = MethodDeclarationRegex.Match(lines[i]);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        return null;
    }

    private string ExtractProjectName(string filePath)
    {
        // Try to find project name from directory structure
        var parts = filePath.Split(Path.DirectorySeparatorChar);
        
        // Look for a .csproj file in parent directories
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

        // Fallback: use parent folder name
        return parts.Length >= 2 ? parts[^2] : "Unknown";
    }

    /// <summary>
    /// Converts a SpecFlow/Reqnroll pattern to a regex pattern for matching
    /// Handles various patterns:
    /// - Already full regex patterns: ^pattern$, (?i)pattern
    /// - Regex capture groups: (.+), (.*), (\d+), ([^']*)
    /// - Alternation groups: (option1|option2)
    /// - SpecFlow/Reqnroll placeholders: {string}, {int}, {decimal}
    /// - Quoted parameters: 'value' or "value"
    /// </summary>
    public string ConvertToRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "^$";
        
        var result = pattern.Trim();
        
        // Check if the pattern is already a full regex (has anchors or regex flags)
        bool isAlreadyRegex = result.StartsWith("^") || 
                              result.EndsWith("$") || 
                              result.StartsWith("(?") ||
                              result.Contains("(?i)");
        
        if (isAlreadyRegex)
        {
            // Pattern is already a regex - just ensure it has anchors
            // Remove existing anchors first to normalize
            var cleanPattern = result.TrimStart('^').TrimEnd('$');
            
            // Handle case-insensitive flag
            if (cleanPattern.StartsWith("(?i)"))
            {
                cleanPattern = cleanPattern.Substring(4);
            }
            
            
            // Add back anchors
            return "^" + cleanPattern + "$";
        }
        
        // Check if pattern contains regex-like constructs or custom type placeholders
        bool hasRegexConstructs = pattern.Contains("(.") || 
                                  pattern.Contains("(\\d") ||
                                  pattern.Contains("([^") ||
                                  (pattern.Contains("|") && pattern.Contains("(")) ||
                                  pattern.Contains("{string}") ||
                                  pattern.Contains("{int}") ||
                                  pattern.Contains("\\(") ||  // Escaped literal parenthesis
                                  pattern.Contains("\\)") ||  // Escaped literal parenthesis
                                  pattern.Contains("\\.") ||  // Escaped literal period
                                  Regex.IsMatch(pattern, @"\{[A-Z]");  // Custom type placeholders like {FormConfiguratorArea}
        
        
        if (hasRegexConstructs)
        {
            // Pattern has regex constructs - carefully process it
            result = ProcessPatternWithRegexConstructs(pattern);
        }
        else
        {
            // Simple pattern - escape and convert placeholders
            result = Regex.Escape(pattern);
            
            // Handle SpecFlow/Reqnroll parameter placeholders (known types)
            result = Regex.Replace(result, @"\\\{string\\\}", "(.+)");
            result = Regex.Replace(result, @"\\\{int\\\}", @"(-?\d+)");
            result = Regex.Replace(result, @"\\\{decimal\\\}", @"(-?\d+\.?\d*)");
            result = Regex.Replace(result, @"\\\{float\\\}", @"(-?\d+\.?\d*)");
            result = Regex.Replace(result, @"\\\{word\\\}", @"(\w+)");
            
            // Handle any remaining custom type placeholders like {FormConfiguratorArea}, {EstimateTriageEmailType}.
            // Match permissively (.+) — a custom type can be multi-word, and \w+ would miss it and wrongly
            // flag a used binding as unused.
            result = Regex.Replace(result, @"\\\{[A-Za-z_][A-Za-z0-9_]*\\\}", @"(.+)");
        }
        
        // Ensure anchors
        if (!result.StartsWith("^"))
            result = "^" + result;
        if (!result.EndsWith("$"))
            result = result + "$";
        
        return result;
    }
    
    /// <summary>
    /// Processes a pattern that contains regex constructs
    /// Properly handles:
    /// - Escaped literals like \( \) \. (keep as-is)
    /// - Capture groups like (.*), (option1|option2)
    /// - SpecFlow placeholders like {string}, {int}
    /// </summary>
    private string ProcessPatternWithRegexConstructs(string pattern)
    {
        var result = new StringBuilder();
        int i = 0;
        
        while (i < pattern.Length)
        {
            // Check for escape sequences FIRST (before checking for '(')
            // This handles \( \) \. etc. which are literal characters
            if (pattern[i] == '\\' && i + 1 < pattern.Length)
            {
                // This is an escape sequence - keep it as-is
                result.Append(pattern[i]);     // the backslash
                result.Append(pattern[i + 1]); // the escaped character
                i += 2;
                continue;
            }
            
            // Check for SpecFlow/Reqnroll placeholders like {string}, {int}, {CustomType}
            if (pattern[i] == '{')
            {
                var endBrace = pattern.IndexOf('}', i);
                if (endBrace > i)
                {
                    var placeholder = pattern.Substring(i + 1, endBrace - i - 1);
                    var placeholderLower = placeholder.ToLower();
                    switch (placeholderLower)
                    {
                        case "string":
                            result.Append("(.+)");
                            break;
                        case "int":
                            result.Append(@"(-?\d+)");
                            break;
                        case "decimal":
                        case "float":
                            result.Append(@"(-?\d+\.?\d*)");
                            break;
                        case "word":
                            result.Append(@"(\w+)");
                            break;
                        default:
                            // Custom type placeholder like {FormConfiguratorArea}, {EstimateTriageEmailType}.
                            // These are Reqnroll/SpecFlow custom type transforms whose real regex we can't know
                            // here — so match permissively (.+, not \w+). \w+ only matches a single word, which
                            // fails multi-word values and would wrongly flag a USED binding as unused.
                            result.Append(@"(.+)");
                            break;
                    }
                    i = endBrace + 1;
                    continue;
                }
            }
            
            // Check for regex group start (unescaped parenthesis)
            if (pattern[i] == '(')
            {
                // Find the matching closing parenthesis, accounting for nested groups and escape sequences
                int depth = 1;
                int start = i;
                i++;
                while (i < pattern.Length && depth > 0)
                {
                    // Skip escape sequences inside the group
                    if (pattern[i] == '\\' && i + 1 < pattern.Length)
                    {
                        i += 2;
                        continue;
                    }
                    
                    if (pattern[i] == '(') depth++;
                    else if (pattern[i] == ')') depth--;
                    i++;
                }
                
                // Extract the group content (including parentheses)
                var group = pattern.Substring(start, i - start);
                result.Append(group); // Keep regex groups as-is
                continue;
            }
            
            // Check for characters that need escaping (not in a regex context)
            if (".+*?[]{}|^$".Contains(pattern[i]))
            {
                // Check if this is part of a regex construct like .* or .+
                if (pattern[i] == '.' && i + 1 < pattern.Length && (pattern[i + 1] == '*' || pattern[i + 1] == '+'))
                {
                    // This is .* or .+ - keep as-is
                    result.Append(pattern[i]);
                }
                else
                {
                    // Escape the special character
                    result.Append('\\');
                    result.Append(pattern[i]);
                }
            }
            else
            {
                result.Append(pattern[i]);
            }
            
            i++;
        }
        
        return result.ToString();
    }

    /// <summary>
    /// Escapes regex special characters but preserves regex capture groups
    /// </summary>
    private string EscapeNonRegexChars(string pattern)
    {
        var result = new StringBuilder();
        var inGroup = 0;
        var inCharClass = false;
        
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            
            // Track if we're inside a capture group or character class
            if (c == '(' && !inCharClass) inGroup++;
            else if (c == ')' && !inCharClass) inGroup--;
            else if (c == '[' && inGroup > 0) inCharClass = true;
            else if (c == ']' && inCharClass) inCharClass = false;
            
            // If inside a regex group, don't escape
            if (inGroup > 0 || inCharClass)
            {
                result.Append(c);
            }
            else
            {
                // Escape special regex chars outside of groups
                if (".^$|?*+{}\\".Contains(c) && c != '(' && c != ')')
                {
                    result.Append('\\');
                }
                result.Append(c);
            }
        }
        
        return result.ToString();
    }

    /// <summary>
    /// Checks if a step text matches a step definition pattern
    /// Handles:
    /// - Direct regex matching
    /// - Scenario Outline placeholders like &lt;Is Data&gt; matching regex groups like (true|false)
    /// - Quoted parameters
    /// - Alternation patterns like (customer|primary)
    /// </summary>
    public bool IsMatch(string stepText, string regexPattern)
    {
        try
        {
            // Normalize step text (remove keyword)
            var normalizedStep = NormalizeStepText(stepText);
            
            // Handle case-insensitive patterns
            var regexOptions = RegexOptions.IgnoreCase;
            var patternToUse = regexPattern;
            
            // Remove (?i) flag if present (we handle case insensitivity via RegexOptions)
            if (patternToUse.Contains("(?i)"))
            {
                patternToUse = patternToUse.Replace("(?i)", "");
            }
            
            // First try: Direct regex match
            try
            {
                if (Regex.IsMatch(normalizedStep, patternToUse, regexOptions))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Invalid regex - try alternative approaches
            }
            
            // Second try: Replace Scenario Outline placeholders <...> with regex wildcards
            // This handles cases like "user checks <Is Data> returned" matching "^user checks (true|false) returned$"
            if (normalizedStep.Contains('<') && normalizedStep.Contains('>'))
            {
                // Replace placeholders with wildcard pattern
                var placeholderReplaced = Regex.Replace(normalizedStep, @"<[^>]+>", "(.+)");
                
                // Try matching with placeholders as wildcards
                var patternWithoutAnchors = patternToUse.TrimStart('^').TrimEnd('$');
                
                // Create a simpler pattern for structural comparison
                // Replace specific capture groups with generic (.+) for comparison
                var genericPattern = Regex.Replace(patternWithoutAnchors, @"\([^)]+\)", "(.+)");
                var genericStep = "^" + Regex.Escape(Regex.Replace(normalizedStep, @"<[^>]+>", "PLACEHOLDER"))
                    .Replace("PLACEHOLDER", "(.+)") + "$";
                
                // Compare structures
                try
                {
                    var normalizedPattern = "^" + genericPattern + "$";
                    if (string.Equals(
                        Regex.Replace(genericPattern, @"\s+", " ").Trim(),
                        Regex.Replace(Regex.Replace(normalizedStep, @"<[^>]+>", "(.+)"), @"\s+", " ").Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch { }
            }
            
            // Third try: For quoted string parameters
            // Match 'value' in step text against pattern expecting quoted strings
            if (normalizedStep.Contains("'") || normalizedStep.Contains("\""))
            {
                // Replace quoted values with generic placeholder
                var stepWithPlaceholders = Regex.Replace(normalizedStep, @"'[^']*'", "QUOTED");
                stepWithPlaceholders = Regex.Replace(stepWithPlaceholders, @"""[^""]*""", "QUOTED");
                
                var patternWithPlaceholders = Regex.Replace(patternToUse.TrimStart('^').TrimEnd('$'), @"\([^)]+\)", "QUOTED");
                
                if (string.Equals(
                    Regex.Replace(stepWithPlaceholders, @"\s+", " ").Trim(),
                    Regex.Replace(patternWithPlaceholders, @"\s+", " ").Trim(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            
            // Fourth try: For numeric parameters
            // Replace numbers in step text and try matching
            var stepWithNumbers = Regex.Replace(normalizedStep, @"\b\d+\b", "NUM");
            var patternWithNumbers = Regex.Replace(patternToUse.TrimStart('^').TrimEnd('$'), @"\(\\?d[\+\*]?\)", "NUM");
            patternWithNumbers = Regex.Replace(patternWithNumbers, @"\(-\?\\?d[\+\*]?\)", "NUM");
            
            if (string.Equals(
                Regex.Replace(stepWithNumbers, @"\s+", " ").Trim(),
                Regex.Replace(patternWithNumbers, @"\s+", " ").Trim(),
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Converts a regex pattern back to readable text by replacing capture groups with placeholders
    /// </summary>
    private string ConvertRegexToText(string regexPattern)
    {
        var result = regexPattern;
        
        // Replace capture groups with a placeholder
        // (true|false) -> PARAM
        // ([^']*) -> PARAM
        // (.+) -> PARAM
        // (\d+) -> PARAM
        result = Regex.Replace(result, @"\([^)]+\)", "PLACEHOLDER");
        
        // Unescape common escaped characters
        result = result.Replace(@"\.", ".");
        result = result.Replace(@"\?", "?");
        result = result.Replace(@"\*", "*");
        result = result.Replace(@"\+", "+");
        result = result.Replace(@"\'", "'");
        result = result.Replace(@"\""", "\"");
        
        return result;
    }

    /// <summary>
    /// Normalizes step text by removing the Gherkin keyword
    /// </summary>
    public string NormalizeStepText(string stepText)
    {
        var keywords = new[] { "Given ", "When ", "Then ", "And ", "But ", "* " };
        foreach (var keyword in keywords)
        {
            if (stepText.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return stepText.Substring(keyword.Length).Trim();
            }
        }
        return stepText.Trim();
    }
}
