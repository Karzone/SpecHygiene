using SpecHygiene.Analysis.Reqnroll;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// The cascade: what dies once the dead bindings are deleted. The two that matter are the transitive
/// chain (which their single-pass orphan check cannot find) and the negative — a helper also reachable
/// from live code must survive, or the cascade recommends deleting working code.
/// </summary>
public sealed class DeadBindingCascadeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bdd-casc-" + Guid.NewGuid().ToString("N"));

    public DeadBindingCascadeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly UnusedCodeAnalysisSettings Settings = new()
    {
        Enabled = true,
        IncludePrivateMethods = true,
    };

    private (List<CascadeOrphan> Orphans, List<StepDefinitionInfo> Bindings) Run(string code, params string[] deadMethodNames)
    {
        var file = Path.Combine(_dir, "Steps.cs");
        File.WriteAllText(file, code.ReplaceLineEndings("\n"));

        var found = new SyntacticBindingDiscovery().Discover(new[] { file });
        var dead = found.Bindings.Where(b => deadMethodNames.Contains(b.MethodName)).ToList();
        var orphans = new DeadBindingCascadeAnalyzer(Settings).Analyze(new[] { file }, dead);
        return (orphans, found.Bindings);
    }

    [Fact]
    public void A_helper_reachable_only_from_a_dead_binding_is_an_orphan()
    {
        var (orphans, _) = Run("""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"a dead step")]
                public void DeadStep() { HelperA(); }

                private void HelperA() { }
            }
            """, "DeadStep");

        var o = Assert.Single(orphans);
        Assert.Equal("HelperA", o.MethodName);
        Assert.Equal(1, o.Round);
    }

    [Fact]
    public void The_cascade_is_transitive()
    {
        // binding -> A -> B. Their single-pass check finds A only and tells you to re-run for B.
        var (orphans, _) = Run("""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"a dead step")]
                public void DeadStep() { HelperA(); }

                private void HelperA() { HelperB(); }
                private void HelperB() { }
            }
            """, "DeadStep");

        Assert.Equal(2, orphans.Count);
        Assert.Equal("HelperA", orphans.Single(o => o.Round == 1).MethodName);
        Assert.Equal("HelperB", orphans.Single(o => o.Round == 2).MethodName);
    }

    [Fact]
    public void A_helper_also_called_from_a_live_binding_survives()
    {
        // The negative that keeps this honest: HelperA is shared, so deleting the dead binding does
        // not orphan it. Getting this wrong means recommending the deletion of working code.
        var (orphans, _) = Run("""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"a dead step")]
                public void DeadStep() { HelperA(); }

                [Given(@"a live step")]
                public void LiveStep() { HelperA(); }

                private void HelperA() { }
            }
            """, "DeadStep");

        Assert.Empty(orphans);
    }

    [Fact]
    public void A_helper_dead_on_its_own_is_not_attributed_to_the_cascade()
    {
        // The differential test: UnrelatedDeadHelper is called by NOTHING — it is dead for its own
        // reasons, independent of the seed. It belongs to the base unused-code analysis, not here.
        // Reporting it would falsely blame the dead binding (the real the sample suite ProgressionConfig case).
        var (orphans, _) = Run("""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"a dead step")]
                public void DeadStep() { }   // calls no helper

                private void UnrelatedDeadHelper() { }   // called by nothing, seed or no seed
            }
            """, "DeadStep");

        Assert.Empty(orphans);
    }

    [Fact]
    public void An_independently_dead_helper_does_not_inflate_the_round_of_a_real_orphan()
    {
        // A genuine orphan (HelperA, reachable only via the dead binding) must be round 1 even when an
        // unrelated independently-dead helper also exists — the baseline pre-seed keeps rounds clean.
        var (orphans, _) = Run("""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"a dead step")]
                public void DeadStep() { HelperA(); }

                private void HelperA() { }
                private void UnrelatedDeadHelper() { }
            }
            """, "DeadStep");

        var o = Assert.Single(orphans);
        Assert.Equal("HelperA", o.MethodName);
        Assert.Equal(1, o.Round);
    }

    [Fact]
    public void A_helper_referenced_from_a_field_initializer_survives()
    {
        // References outside any method are live roots — deleting a binding never removes them.
        var (orphans, _) = Run("""
            using System;
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                private readonly Action _hook = Setup;

                [Given(@"a dead step")]
                public void DeadStep() { Setup(); }

                private static void Setup() { }
            }
            """, "DeadStep");

        Assert.Empty(orphans);
    }

    [Fact]
    public void Nothing_cascades_when_no_binding_is_dead()
    {
        var (orphans, _) = Run("""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"a live step")]
                public void LiveStep() { HelperA(); }

                private void HelperA() { }
            }
            """);

        Assert.Empty(orphans);
    }

    [Fact]
    public void An_orphan_that_the_base_analyzer_would_not_report_is_not_reported_here_either()
    {
        // Public methods are the external surface; the base analyzer skips them unless opted in, and the
        // cascade must not be less conservative than the analyzer it extends.
        var (orphans, _) = Run("""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"a dead step")]
                public void DeadStep() { PublicHelper(); }

                public void PublicHelper() { }
            }
            """, "DeadStep");

        Assert.Empty(orphans);   // IncludePublicMembers is false
    }
}
