using SpecHygiene.Analysis;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// WPF event handlers are wired in XAML, not C#, so a C#-only reference graph reads them as dead —
/// the dominant false-positive on any WPF-backed project. XAML references SUPPRESS an unused verdict,
/// but only from reference positions (attribute values, markup extensions), never attribute/element
/// names — otherwise a dead method sharing a name with a common attribute word would silently vanish.
/// These pin both halves of that contract.
/// </summary>
public sealed class UnusedCodeXamlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-xaml-" + Guid.NewGuid().ToString("N"));

    public UnusedCodeXamlTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private UnusedCodeReport Run(string code, string xaml)
    {
        File.WriteAllText(Path.Combine(_dir, "Code.cs"), code.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(_dir, "View.xaml"), xaml.ReplaceLineEndings("\n"));
        var settings = new UnusedCodeAnalysisSettings { Enabled = true, IncludePrivateMethods = true, IncludeInternalMethods = true };
        return new UnusedCodeAnalyzer(settings).Analyze(new[] { _dir });
    }

    [Fact]
    public void Event_handler_wired_in_xaml_is_not_reported_unused()
    {
        // Browse_Click is referenced only from XAML (attribute VALUE) — a real usage, so not dead.
        var r = Run(
            code: """
                public class MainWindow
                {
                    private void Browse_Click(object sender, System.EventArgs e) { }
                }
                """,
            xaml: """
                <Window><Button Click="Browse_Click" Content="Hello" /></Window>
                """);

        Assert.DoesNotContain(r.UnusedMethods, m => m.Name == "Browse_Click");
    }

    [Fact]
    public void Dead_method_sharing_a_name_with_an_attribute_name_is_still_reported()
    {
        // "Content" appears in the XAML ONLY as an attribute NAME (Content="Hello"), never as a value.
        // A genuinely-dead Content() method must therefore still be flagged — the guardrail against
        // whole-text extraction silently suppressing real dead code.
        var r = Run(
            code: """
                public class MainWindow
                {
                    private void Content() { }
                }
                """,
            xaml: """
                <Window><Button Content="Hello" /></Window>
                """);

        Assert.Contains(r.UnusedMethods, m => m.Name == "Content");
    }
}
