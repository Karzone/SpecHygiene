using SpecHygiene.Analysis;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// Extension methods are the dominant false-positive class in the reference-light compilation: a
/// private/internal extension helper called via <c>receiver.Method()</c> can read as unused. These
/// pin the real behaviour so the fix is verified, not assumed.
/// </summary>
public sealed class UnusedCodeExtensionMethodTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bdd-ext-" + Guid.NewGuid().ToString("N"));

    public UnusedCodeExtensionMethodTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private UnusedCodeReport Run(string code)
    {
        File.WriteAllText(Path.Combine(_dir, "Code.cs"), code.ReplaceLineEndings("\n"));
        var settings = new UnusedCodeAnalysisSettings { Enabled = true, IncludePrivateMethods = true, IncludeInternalMethods = true };
        return new UnusedCodeAnalyzer(settings).Analyze(new[] { _dir });
    }

    [Fact]
    public void A_private_extension_helper_called_via_receiver_syntax_is_not_unused()
    {
        // The exact the sample suite StringExtensions shape: a private extension method invoked as an extension by a
        // public one in the same class. It is USED and must not be reported.
        var r = Run("""
            public static class StringExtensions
            {
                public static string ReplaceUnderscoreCharWithFullStop(this string textInput) => textInput.ReplaceWithFullStop("_");
                private static string ReplaceWithFullStop(this string textInput, string charToRemove) => textInput.Replace(charToRemove, ".");
            }
            """);

        Assert.DoesNotContain(r.UnusedMethods, m => m.Name == "ReplaceWithFullStop");
    }

    [Fact]
    public void A_private_extension_helper_with_no_call_site_anywhere_is_still_unused()
    {
        // The fix must not blind the analyzer: an extension method nothing calls is still dead.
        var r = Run("""
            public static class StringExtensions
            {
                public static string Used(this string s) => s.Trim();
                private static string TrulyDead(this string s, string x) => s.Replace(x, ".");
            }
            """);

        Assert.Contains(r.UnusedMethods, m => m.Name == "TrulyDead");
    }
}
