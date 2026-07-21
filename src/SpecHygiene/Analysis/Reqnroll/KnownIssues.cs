// Pure, unit-testable "known/approved issue" annotation, driven by a simple CSV.
//
// Some dead bindings are known and accepted - e.g. a step whose only feature usage is
// commented out, or a binding deliberately kept for a reason tracked elsewhere. Rather than
// have them show up every run looking like fresh actionable dead code, list them in a
// `known-issues.csv` (committed alongside the tool) and the run keeps them in the report but
// flags each with `KnownIssue=true` + the supplied comment, and counts them separately from the
// genuinely-actionable dead bindings.
//
// CSV format (header required): SourceFile,MethodName,Comment
//   - SourceFile: a path SUFFIX of the binding's source file (e.g. just "AllocationSteps.cs"
//     or a fuller "API/Acme.Api.Motor/Steps/AllocationSteps.cs"). Matched case-insensitively
//     with '/' and '\' treated the same. Leave empty to match the method in ANY file.
//   - MethodName: the binding method's name, matched exactly (case-sensitive - C# identifiers).
//   - Comment: free text (may contain commas); everything after the second comma is the comment.
//   - Blank lines and lines beginning with '#' are ignored.
using System.Linq;

namespace SpecHygiene.Analysis.Reqnroll
{
    public sealed record KnownIssue(string SourceFile, string MethodName, string Comment);

    public static class KnownIssues
    {
        /// <summary>Parse known-issues CSV text into rows. Skips the header, blank lines, and '#' comments.</summary>
        public static IReadOnlyList<KnownIssue> Parse(string? csvText)
        {
            var result = new List<KnownIssue>();
            if (string.IsNullOrWhiteSpace(csvText)) return result;

            bool headerSkipped = false;
            foreach (var rawLine in csvText.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                // Split into at most 3 fields so commas inside the free-text Comment are preserved.
                var parts = line.Split(new[] { ',' }, 3);
                var sourceFile = Unquote(parts.Length > 0 ? parts[0] : "");
                var methodName = Unquote(parts.Length > 1 ? parts[1] : "");
                var comment = Unquote(parts.Length > 2 ? parts[2] : "");

                // Tolerate a header row in any casing ("SourceFile,MethodName,Comment"), skipping the
                // first row that looks like it, so a hand-maintained file with the documented header
                // Just Works without the header being mistaken for a real entry.
                if (!headerSkipped &&
                    sourceFile.Equals("SourceFile", StringComparison.OrdinalIgnoreCase) &&
                    methodName.Equals("MethodName", StringComparison.OrdinalIgnoreCase))
                {
                    headerSkipped = true;
                    continue;
                }
                headerSkipped = true; // only the very first data-or-header line can be the header

                if (methodName.Length == 0) continue; // a row with no method name can't match anything
                result.Add(new KnownIssue(sourceFile, methodName, comment));
            }

            return result;
        }

        /// <summary>
        /// Returns the comment of the first known-issue row matching the given binding, or null if
        /// none match. Match = MethodName exact (case-sensitive) AND (row SourceFile empty, or the
        /// binding's source file path ends with the row's SourceFile, compared case-insensitively
        /// with separators normalised).
        /// </summary>
        public static string? Match(IReadOnlyList<KnownIssue> issues, string? bindingSourceFile, string bindingMethodName)
        {
            if (issues is null || issues.Count == 0) return null;

            var normBinding = NormalizePath(bindingSourceFile);
            foreach (var issue in issues)
            {
                if (!string.Equals(issue.MethodName, bindingMethodName, StringComparison.Ordinal)) continue;
                if (issue.SourceFile.Length == 0)
                    return issue.Comment; // method-only rule
                if (normBinding.EndsWith(NormalizePath(issue.SourceFile), StringComparison.OrdinalIgnoreCase))
                    return issue.Comment;
            }
            return null;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                s = s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
            return s;
        }

        private static string NormalizePath(string? p) => (p ?? "").Replace('\\', '/').Trim();
    }
}
