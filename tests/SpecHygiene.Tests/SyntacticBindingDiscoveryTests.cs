using SpecHygiene.Analysis.Reqnroll;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// Pins the discovery gaps the regex parser could not express: bare [Given] (R9), [Binding] gating
/// with inheritance (R11), enum + transform parameter types (R6/R7), and correct reading of verbatim
/// and escaped string literals.
/// </summary>
public sealed class SyntacticBindingDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bdd-disc-" + Guid.NewGuid().ToString("N"));

    public SyntacticBindingDiscoveryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SyntacticBindingDiscovery.Result Discover(string code)
    {
        var f = Path.Combine(_dir, "Steps.cs");
        File.WriteAllText(f, code.ReplaceLineEndings("\n"));
        return new SyntacticBindingDiscovery().Discover(new[] { f });
    }

    // ---- R9: bare [Given] — INVISIBLE to the regex parser, which required ("...") -------------

    [Fact]
    public void Bare_attribute_with_no_pattern_is_discovered()
    {
        var r = Discover("""
            [Binding]
            public class S
            {
                [Given]
                public void TheOrderIsCreated() { }
            }
            """);

        var b = Assert.Single(r.Bindings);
        Assert.Equal("TheOrderIsCreated", b.MethodName);
        Assert.Equal("", b.Pattern);              // method-name convention; empty -> indeterminate downstream
        Assert.Equal(StepDefinitionType.Given, b.Type);
    }

    [Fact]
    public void Interpolated_pattern_is_flagged_unresolvable_not_a_bare_convention()
    {
        // [Given($@"…{Const}…")]: the argument is present but not a readable literal. It must be
        // flagged UnresolvablePattern (-> indeterminate), NOT mistaken for a bare convention binding
        // with an empty pattern — the the sample suite #5/#6 false positives.
        var r = Discover("""
            [Binding]
            public class S
            {
                const string Region = "Germany|Italy";
                [Given($@"^the user is in {Region} region$")]
                public void M(string region) { }
            }
            """);

        var b = Assert.Single(r.Bindings);
        Assert.True(b.UnresolvablePattern);
    }

    [Fact]
    public void A_plain_literal_is_not_flagged_unresolvable()
    {
        var r = Discover("""
            [Binding]
            public class S
            {
                [Given("the user is ready")]
                public void M() { }
            }
            """);

        var b = Assert.Single(r.Bindings);
        Assert.False(b.UnresolvablePattern);
        Assert.Equal("the user is ready", b.Pattern);
    }

    // ---- string literals the regex mis-read ---------------------------------------------------

    [Fact]
    public void Verbatim_literal_with_escaped_quotes_reads_correctly()
    {
        // The old regex @?"(.+?)" stops at the first inner quote. Roslyn returns the real value.
        var r = Discover("""
            [Binding]
            public class S
            {
                [Given(@"the ""quoted"" value")]
                public void M() { }
            }
            """);

        Assert.Equal("the \"quoted\" value", Assert.Single(r.Bindings).Pattern);
    }

    [Fact]
    public void Regex_pattern_with_backslashes_survives_verbatim()
    {
        var r = Discover("""
            [Binding]
            public class S
            {
                [Given(@"^I have (\d+) items$")]
                public void M() { }
            }
            """);

        Assert.Equal(@"^I have (\d+) items$", Assert.Single(r.Bindings).Pattern);
    }

    // ---- R11: [Binding] gating, honouring inheritance -----------------------------------------

    [Fact]
    public void Binding_inherited_from_a_base_class_counts()
    {
        var r = Discover("""
            [Binding]
            public class BaseSteps { }

            public class DerivedSteps : BaseSteps
            {
                [Given(@"a step")]
                public void M() { }
            }
            """);

        Assert.Single(r.Bindings);
        Assert.Empty(r.Unconfirmed);
    }

    [Fact]
    public void Step_attribute_in_a_class_that_is_definitively_not_a_binding_is_not_a_binding()
    {
        // Every ancestor is in source and none carries [Binding] -> Reqnroll never scans it.
        var r = Discover("""
            public class Helper
            {
                [Given(@"a step")]
                public void M() { }
            }
            """);

        Assert.Empty(r.Bindings);
        Assert.Empty(r.Unconfirmed);
    }

    [Fact]
    public void Unconfirmable_binding_status_is_reported_not_guessed()
    {
        // The base is declared outside the scanned source, so it MIGHT carry [Binding]. We must not
        // claim it either way — this is the case that would otherwise become a false dead.
        var r = Discover("""
            public class DerivedSteps : SomeExternalBase
            {
                [Given(@"a step")]
                public void M() { }
            }
            """);

        Assert.Empty(r.Bindings);
        var (binding, reason) = Assert.Single(r.Unconfirmed);
        Assert.Equal("M", binding.MethodName);
        Assert.Contains("[Binding] not confirmable", reason);
    }

    // ---- R6: enum parameter types from source -------------------------------------------------

    [Fact]
    public void Enum_in_source_becomes_a_case_insensitive_alternation_fragment()
    {
        var r = Discover("""
            public enum DamageEntity { Bumper, Door }

            [Binding]
            public class S
            {
                [Given(@"the damage is {DamageEntity}")]
                public void M(DamageEntity e) { }
            }
            """);

        Assert.True(r.ParameterTypeFragments.ContainsKey("DamageEntity"));
        Assert.Equal("(?i:Bumper|Door)", r.ParameterTypeFragments["DamageEntity"]);

        // End to end: the binding now compiles and matches, instead of being indeterminate.
        var binding = new BindingMatcher(r.ParameterTypeFragments).Compile(r.Bindings[0]);
        Assert.False(binding.IsIndeterminate);
        Assert.True(BindingMatcher.Matches(binding, "the damage is bumper"));
        Assert.False(BindingMatcher.Matches(binding, "the damage is Roof"));
    }

    // ---- R7: [StepArgumentTransformation] -----------------------------------------------------

    [Fact]
    public void Named_transformation_registers_its_placeholder_with_the_regex_verbatim()
    {
        var r = Discover("""
            [Binding]
            public class S
            {
                [StepArgumentTransformation(@"\d{4}-\d{2}-\d{2}", Name = "isoDate")]
                public DateTime Transform(string s) => DateTime.Parse(s);
            }
            """);

        Assert.Equal(@"\d{4}-\d{2}-\d{2}", r.ParameterTypeFragments["isoDate"]);
    }

    [Fact]
    public void Unnamed_transformation_is_named_after_its_return_type()
    {
        var r = Discover("""
            [Binding]
            public class S
            {
                [StepArgumentTransformation(@"[A-Z]{3}")]
                public CurrencyCode Transform(string s) => new(s);
            }
            """);

        Assert.Equal("[A-Z]{3}", r.ParameterTypeFragments["CurrencyCode"]);
    }

    // ---- built-ins are not clobbered ----------------------------------------------------------

    [Fact]
    public void Discovered_fragments_extend_the_builtins_rather_than_replacing_them()
    {
        var r = Discover("""
            public enum E { A }

            [Binding]
            public class S
            {
                [Given(@"x")]
                public void M() { }
            }
            """);

        Assert.True(r.ParameterTypeFragments.ContainsKey("int"));     // built-in survives
        Assert.True(r.ParameterTypeFragments.ContainsKey("Int32"));
        Assert.True(r.ParameterTypeFragments.ContainsKey("E"));       // discovered added
    }

    // ---- aliases: several step attributes on one method ---------------------------------------

    [Fact]
    public void Multiple_step_attributes_on_one_method_yield_one_binding_each()
    {
        var r = Discover("""
            [Binding]
            public class S
            {
                [When(@"the task runs")]
                [Then(@"the task runs")]
                public void M() { }
            }
            """);

        Assert.Equal(2, r.Bindings.Count);
        Assert.Contains(r.Bindings, b => b.Type == StepDefinitionType.When);
        Assert.Contains(r.Bindings, b => b.Type == StepDefinitionType.Then);
    }
}
