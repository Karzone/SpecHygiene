using System.Text.Json;

namespace SpecHygiene.Analysis;

/// <summary>
/// Resolves step definition assemblies by parsing reqnroll.json or specflow.json configuration files.
/// This allows us to only scan the assemblies that are actually referenced by each project,
/// significantly improving performance for large solutions.
/// </summary>
public class StepAssemblyResolver
{
    private static readonly string[] ConfigFileNames = 
    { 
        "reqnroll.json", 
        "specflow.json" 
    };

    private readonly Dictionary<string, List<string>> _projectAssemblyCache = new();
    private readonly Dictionary<string, string> _assemblyToPathCache = new();

    /// <summary>
    /// Discovers all step assembly mappings in the solution
    /// </summary>
    public void DiscoverAssemblies(IEnumerable<string> solutionPaths)
    {
        Console.WriteLine("   Discovering step assembly references...");
        
        foreach (var solutionPath in solutionPaths)
        {
            if (!Directory.Exists(solutionPath))
                continue;

            // Find all project directories
            var projectDirs = Directory.GetDirectories(solutionPath, "*", SearchOption.TopDirectoryOnly)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .Where(d => Directory.GetFiles(d, "*.csproj").Any());

            foreach (var projectDir in projectDirs)
            {
                var projectName = Path.GetFileName(projectDir);
                
                // Cache the assembly name to path mapping
                _assemblyToPathCache[projectName] = projectDir;
                
                // Parse the config file if it exists
                var referencedAssemblies = ParseStepAssemblies(projectDir);
                if (referencedAssemblies.Any())
                {
                    _projectAssemblyCache[projectName] = referencedAssemblies;
                }
            }
        }

        Console.WriteLine($"      Found {_assemblyToPathCache.Count} total projects");
        Console.WriteLine($"      Found {_projectAssemblyCache.Count} test projects with reqnroll.json/specflow.json");
        
        // Show which test projects reference which step assemblies
        if (_projectAssemblyCache.Any())
        {
            Console.WriteLine($"      Test project ? Step assemblies:");
            foreach (var kvp in _projectAssemblyCache)
            {
                Console.WriteLine($"        - {kvp.Key} ? [{string.Join(", ", kvp.Value)}]");
            }
        }
    }

    /// <summary>
    /// Gets the directories to scan for step definitions for a specific project.
    /// Returns only the directories of referenced step assemblies.
    /// </summary>
    public List<string> GetStepDefinitionPaths(string projectPath, IEnumerable<string> fallbackPaths)
    {
        var projectName = Path.GetFileName(projectPath);
        var result = new List<string>();

        // Always include the project's own directory (it may have its own steps)
        result.Add(projectPath);

        // Check if this project has configured step assemblies
        if (_projectAssemblyCache.TryGetValue(projectName, out var referencedAssemblies))
        {
            foreach (var assembly in referencedAssemblies)
            {
                // Find the path for this assembly
                if (_assemblyToPathCache.TryGetValue(assembly, out var assemblyPath))
                {
                    if (!result.Contains(assemblyPath))
                    {
                        result.Add(assemblyPath);
                    }
                }
            }

            return result;
        }

        // Fallback: return all paths if no config found
        return fallbackPaths.ToList();
    }

    /// <summary>
    /// Gets all step definition paths needed for a set of projects.
    /// Combines all referenced assemblies from all projects.
    /// </summary>
    public List<string> GetAllStepDefinitionPaths(IEnumerable<string> projectPaths, IEnumerable<string> fallbackPaths)
    {
        var allPaths = new HashSet<string>();
        var hasAnyConfig = false;

        foreach (var projectPath in projectPaths)
        {
            var projectName = Path.GetFileName(projectPath);

            // Always include the project itself
            allPaths.Add(projectPath);

            // Check if this project has configured step assemblies
            if (_projectAssemblyCache.TryGetValue(projectName, out var referencedAssemblies))
            {
                hasAnyConfig = true;
                
                foreach (var assembly in referencedAssemblies)
                {
                    if (_assemblyToPathCache.TryGetValue(assembly, out var assemblyPath))
                    {
                        allPaths.Add(assemblyPath);
                    }
                }
            }
        }

        // If no projects had config files, fall back to all paths
        if (!hasAnyConfig)
        {
            Console.WriteLine("      ??  No reqnroll.json/specflow.json found, scanning all projects");
            return fallbackPaths.ToList();
        }

        return allPaths.ToList();
    }

    /// <summary>
    /// Gets all project paths that have reqnroll.json/specflow.json configuration.
    /// These are typically test projects that have feature files.
    /// </summary>
    public List<string> GetAllTestProjectPaths()
    {
        var testProjects = new List<string>();
        
        foreach (var kvp in _projectAssemblyCache)
        {
            if (_assemblyToPathCache.TryGetValue(kvp.Key, out var projectPath))
            {
                testProjects.Add(projectPath);
            }
        }
        
        return testProjects;
    }

    /// <summary>
    /// Gets all discovered project paths (both test projects and step assembly projects).
    /// This is useful for scanning feature files across all projects.
    /// </summary>
    public List<string> GetAllDiscoveredProjectPaths()
    {
        return _assemblyToPathCache.Values.ToList();
    }

    /// <summary>
    /// Parses the reqnroll.json or specflow.json file to extract step assemblies
    /// </summary>
    private List<string> ParseStepAssemblies(string projectDir)
    {
        var assemblies = new List<string>();

        foreach (var configFileName in ConfigFileNames)
        {
            var configPath = Path.Combine(projectDir, configFileName);
            
            if (!File.Exists(configPath))
                continue;

            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                
                // Look for stepAssemblies array
                if (doc.RootElement.TryGetProperty("stepAssemblies", out var stepAssemblies))
                {
                    foreach (var item in stepAssemblies.EnumerateArray())
                    {
                        if (item.TryGetProperty("assembly", out var assemblyProp))
                        {
                            var assemblyName = assemblyProp.GetString();
                            if (!string.IsNullOrWhiteSpace(assemblyName))
                            {
                                assemblies.Add(assemblyName);
                            }
                        }
                    }
                }

                // If we found assemblies, no need to check other config files
                if (assemblies.Any())
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      Warning: Failed to parse {configFileName} in {projectDir}: {ex.Message}");
            }
        }

        return assemblies;
    }

    /// <summary>
    /// Gets a summary of discovered assemblies for logging
    /// </summary>
    public string GetDiscoverySummary()
    {
        if (!_projectAssemblyCache.Any())
        {
            return "No step assembly configurations found";
        }

        var lines = new List<string>();
        foreach (var kvp in _projectAssemblyCache)
        {
            lines.Add($"      {kvp.Key} ? [{string.Join(", ", kvp.Value)}]");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
