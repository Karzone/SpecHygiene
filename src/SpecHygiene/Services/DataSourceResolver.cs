using System.Collections.Concurrent;
using System.Globalization;
using SpecHygiene.Models;
using CsvHelper;
using CsvHelper.Configuration;

namespace SpecHygiene.Services;

public class DataSourceResolver
{
    private readonly DataSourceSettings _settings;
    private readonly ConcurrentDictionary<string, DataSourceResult> _cache = new();
    private readonly ConcurrentDictionary<string, DataSourceRowsResult> _rowsCache = new();

    public DataSourceResolver(DataSourceSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Extract @DataSource:value from a tag list. Returns null if absent.
    /// Reqnroll convention: single tag token, colon-separated.
    /// </summary>
    public static string? ExtractDataSourceValue(IEnumerable<string> tags)
    {
        const string prefix = "@DataSource:";
        var tag = tags.FirstOrDefault(t =>
            t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return tag?.Substring(prefix.Length).Trim();
    }

    /// <summary>
    /// Resolve a @DataSource value against the feature file's location and any
    /// configured search paths, then read the CSV header row. Result is cached
    /// by resolved absolute path for the lifetime of this resolver.
    /// </summary>
    public DataSourceResult Resolve(string dataSourceValue, string featureFilePath)
    {
        var resolvedPath = ResolvePath(dataSourceValue, featureFilePath);

        if (resolvedPath is null)
        {
            return new DataSourceResult(
                RequestedValue: dataSourceValue,
                ResolvedPath: null,
                Headers: Array.Empty<string>(),
                Found: false,
                Error: $"CSV not found for @DataSource:{dataSourceValue}");
        }

        return _cache.GetOrAdd(resolvedPath, path => LoadHeaders(dataSourceValue, path));
    }

    private string? ResolvePath(string dataSourceValue, string featureFilePath)
    {
        // Absolute path
        if (Path.IsPathRooted(dataSourceValue) && File.Exists(dataSourceValue))
            return Path.GetFullPath(dataSourceValue);

        // Relative to feature file (Reqnroll default)
        var featureDir = Path.GetDirectoryName(featureFilePath);
        if (featureDir is not null)
        {
            var candidate = Path.Combine(featureDir, dataSourceValue);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        // Additional search paths from settings
        foreach (var searchPath in _settings.AdditionalSearchPaths ?? Array.Empty<string>())
        {
            var candidate = Path.Combine(searchPath, dataSourceValue);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// Resolve the CSV and load every row keyed by header. Used by the dataseed gap analyzer
    /// to build the seed catalog. Cached separately from Resolve() so the Phase 0 header-only
    /// path is unaffected.
    /// </summary>
    public DataSourceRowsResult ResolveRows(string dataSourceValue, string featureFilePath)
    {
        var resolvedPath = ResolvePath(dataSourceValue, featureFilePath);
        if (resolvedPath is null)
        {
            return new DataSourceRowsResult(
                RequestedValue: dataSourceValue,
                ResolvedPath: null,
                Headers: Array.Empty<string>(),
                Rows: Array.Empty<Dictionary<string, string>>(),
                Found: false,
                Error: $"CSV not found for @DataSource:{dataSourceValue}");
        }

        return _rowsCache.GetOrAdd(resolvedPath, path => LoadRows(dataSourceValue, path));
    }

    /// <summary>
    /// Load only the headers without parsing rows. Same path-resolution as Resolve(),
    /// but uses the rows cache so a later ResolveRows on the same path is free.
    /// Used by the dataseed gap analyzer's orphan-file scan when a CSV is unreferenced
    /// but we still want to report its row count.
    /// </summary>
    public DataSourceRowsResult LoadStandaloneCsv(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            return new DataSourceRowsResult(
                RequestedValue: absolutePath,
                ResolvedPath: null,
                Headers: Array.Empty<string>(),
                Rows: Array.Empty<Dictionary<string, string>>(),
                Found: false,
                Error: "File not found");
        }
        var fullPath = Path.GetFullPath(absolutePath);
        return _rowsCache.GetOrAdd(fullPath, p => LoadRows(p, p));
    }

    private static DataSourceRowsResult LoadRows(string requested, string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                TrimOptions = TrimOptions.Trim,
                MissingFieldFound = null,
                BadDataFound = null
            };
            using var csv = new CsvReader(reader, config);
            csv.Read();
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? Array.Empty<string>();

            var rows = new List<Dictionary<string, string>>();
            while (csv.Read())
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in headers)
                {
                    row[header] = csv.GetField(header) ?? string.Empty;
                }
                rows.Add(row);
            }

            return new DataSourceRowsResult(requested, path, headers, rows, Found: true);
        }
        catch (Exception ex)
        {
            return new DataSourceRowsResult(
                requested, path,
                Array.Empty<string>(), Array.Empty<Dictionary<string, string>>(),
                Found: false, Error: ex.Message);
        }
    }

    private static DataSourceResult LoadHeaders(string requested, string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                TrimOptions = TrimOptions.Trim,
                MissingFieldFound = null,
                BadDataFound = null
            };
            using var csv = new CsvReader(reader, config);
            csv.Read();
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? Array.Empty<string>();
            return new DataSourceResult(requested, path, headers, Found: true);
        }
        catch (Exception ex)
        {
            return new DataSourceResult(
                requested, path, Array.Empty<string>(),
                Found: false, Error: ex.Message);
        }
    }
}

public record DataSourceResult(
    string RequestedValue,
    string? ResolvedPath,
    IReadOnlyList<string> Headers,
    bool Found,
    string? Error = null);

public record DataSourceRowsResult(
    string RequestedValue,
    string? ResolvedPath,
    IReadOnlyList<string> Headers,
    IReadOnlyList<Dictionary<string, string>> Rows,
    bool Found,
    string? Error = null);
