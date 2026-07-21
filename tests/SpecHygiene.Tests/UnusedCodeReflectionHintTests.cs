using SpecHygiene.Analysis;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// The reflection-hint qualifier and the scope disclosure. The hint automates the one manual check a
/// private-method dead-code finding still needs; the scope makes a path-based result honest about not
/// being solution-wide.
/// </summary>
public sealed class UnusedCodeReflectionHintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bdd-unused-" + Guid.NewGuid().ToString("N"));

    public UnusedCodeReflectionHintTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private UnusedCodeReport Run(string code)
    {
        File.WriteAllText(Path.Combine(_dir, "Code.cs"), code.ReplaceLineEndings("\n"));
        var settings = new UnusedCodeAnalysisSettings { Enabled = true, IncludePrivateMethods = true };
        return new UnusedCodeAnalyzer(settings).Analyze(new[] { _dir });
    }

    private UnusedCodeInfo Method(UnusedCodeReport r, string name) =>
        r.UnusedMethods.Single(m => m.Name == name);

    [Fact]
    public void An_unused_method_with_no_name_collision_is_flagged_clear()
    {
        var r = Run("""
            public class C
            {
                private void Orphan() { }
                public void Root() { }
            }
            """);

        Assert.Null(Method(r, "Orphan").StringLiteralHint);
    }

    [Fact]
    public void An_unused_method_whose_name_appears_as_a_string_literal_is_hinted()
    {
        // The reflection channel the reference graph cannot see: the name used as a GetMethod key.
        var r = Run("""
            using System.Reflection;
            public class C
            {
                private void Invoked() { }
                public void Root()
                {
                    var m = typeof(C).GetMethod("Invoked", BindingFlags.NonPublic | BindingFlags.Instance);
                    m?.Invoke(this, null);
                }
            }
            """);

        // Still reported unused (the reference graph genuinely has no call), but qualified.
        var info = Method(r, "Invoked");
        Assert.NotNull(info.StringLiteralHint);
        Assert.Equal(1, r.StringLiteralHintCount);
    }

    [Fact]
    public void Nameof_is_a_real_reference_and_does_not_flag_the_method_at_all()
    {
        // nameof resolves to the symbol, so it counts as USED — the method is not in the unused list,
        // and this is exactly why only STRING literals need the hint.
        var r = Run("""
            public class C
            {
                private void Tracked() { }
                public string Root() => nameof(Tracked);
            }
            """);

        Assert.DoesNotContain(r.UnusedMethods, m => m.Name == "Tracked");
    }

    [Fact]
    public void The_hint_annotates_it_does_not_remove_the_finding()
    {
        // A genuinely dead method whose name coincides with an unrelated string must still be reported,
        // just qualified — never silently dropped on a coincidence.
        var r = Run("""
            public class C
            {
                private void Cleanup() { }
                public string Root() => "Cleanup complete";   // unrelated string that happens to contain the name? no —
                public string Other() => "Cleanup";           // exact-match unrelated literal
            }
            """);

        Assert.Contains(r.UnusedMethods, m => m.Name == "Cleanup");   // still dead
        Assert.NotNull(Method(r, "Cleanup").StringLiteralHint);       // but flagged
    }

    [Fact]
    public void The_report_records_the_scanned_roots()
    {
        var r = Run("public class C { private void X() { } }");
        Assert.Contains(_dir, r.ScannedRoots);
        Assert.True(r.TotalFilesScanned >= 1);
    }
}
