using SpecHygiene.Analysis.Reqnroll;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// Pins the matching swap: Reqnroll's full-match rule (R1), grammar routing (the classifier that did
/// not come with the port), built-in parameter types (R5), and the indeterminate bucket that keeps an
/// unevaluable binding from being reported dead.
/// </summary>
public sealed class BindingMatcherTests
{
    private static readonly BindingMatcher Matcher = new();

    private static CompiledBinding Compile(string pattern) =>
        Matcher.Compile(new StepDefinitionInfo { Pattern = pattern, MethodName = "M" });

    private static bool Matches(string pattern, string stepText) =>
        BindingMatcher.Matches(Compile(pattern), stepText);

    // ---- R1: full match, never substring ----------------------------------------------------

    [Fact]
    public void Partial_match_is_rejected()
    {
        // The old ladder's Strategy 3 accepted this unanchored — the confirmed false-"alive" source.
        Assert.False(Matches("I do something", "I do something extra"));
    }

    [Fact]
    public void Exact_text_matches()
    {
        Assert.True(Matches("I do something", "I do something"));
    }

    // ---- classifier: Cucumber Expression vs raw regex ---------------------------------------

    [Fact]
    public void Anchored_pattern_is_treated_as_raw_regex()
    {
        Assert.False(CucumberExpressionDetector.IsCucumberExpression("^user (.*) task$"));
        Assert.True(Matches("^user (.*) task$", "user completes task"));
    }

    [Fact]
    public void Unanchored_pattern_is_treated_as_a_cucumber_expression()
    {
        Assert.True(CucumberExpressionDetector.IsCucumberExpression("I have {int} items"));
        Assert.True(Matches("I have {int} items", "I have 42 items"));
        Assert.False(Matches("I have {int} items", "I have many items"));
    }

    [Fact]
    public void Reqnroll_leaves_a_textually_anchored_alternation_unanchored()
    {
        // R8: "^A|B$" textually starts with ^ and ends with $, so Reqnroll wraps neither — the ^ binds
        // only to the A branch. A step with trailing text still binds. Confirmed against real Reqnroll;
        // getting this wrong produced a false-DEAD for a live binding.
        Assert.True(Matches("^user updates and completes|cancels manual task$",
                            "user updates and completes manual task for the order"));
    }

    // ---- anchorless regexes: routed as regex, and never hang the Cucumber compiler ----------

    [Theory]
    // Real the sample suite patterns that lack ^/$ anchors but ARE regexes. Reqnroll's detector routes them as
    // regex via its (…+)/(…*)/.* and \./\d+ checks. Our old detector saw no anchor, called them
    // Cucumber, fed them to CompileSegment, and hung forever on the stray \ — the the sample suite coverage stall.
    [InlineData(@"create full tables in Framework\.PostcodeAreas")]   // \.
    [InlineData(@"DPA verification should fail with failed count (\d+)")]   // (\d+)
    [InlineData(@"(\d+) days pass since repair completion")]   // leading (\d+)
    public void Anchorless_regex_patterns_route_to_regex_not_cucumber(string pattern)
    {
        Assert.False(CucumberExpressionDetector.IsCucumberExpression(pattern));
    }

    [Fact]
    public void Anchorless_regex_with_capture_group_matches_the_real_step()
    {
        // Routed as regex, (\d+) captures a number, so the live step binds — no longer a false dead.
        Assert.True(Matches(@"DPA verification should fail with failed count (\d+)",
                            "DPA verification should fail with failed count 3"));
        Assert.True(Matches(@"(\d+) days pass since repair completion", "5 days pass since repair completion"));
        Assert.True(Matches(@"create full tables in Framework\.PostcodeAreas",
                            "create full tables in Framework.PostcodeAreas"));
    }

    [Fact]
    public void A_placeholder_keeps_a_pattern_cucumber_even_with_regexish_text()
    {
        // Reqnroll checks {param} BEFORE the regex-construct checks, so the placeholder wins.
        Assert.True(CucumberExpressionDetector.IsCucumberExpression(@"I wait {int} times for \d"));
    }

    [Fact]
    public void A_stray_backslash_in_a_genuine_cucumber_expression_terminates()
    {
        // "match \w+ names" has no anchor, no {param}, and none of Reqnroll's regex signals (\. \d+
        // (x+) .*), so Reqnroll keeps it a Cucumber Expression — it legitimately reaches CompileSegment.
        // Before hardening, the \w spun forever; now it is consumed as a literal escape and compiles.
        var b = Compile(@"match \w+ names");
        Assert.False(b.IsIndeterminate);
        Assert.True(BindingMatcher.Matches(b, "match w+ names"));   // \w -> literal w, per Cucumber escape
    }

    // ---- R5: built-in parameter types, both C# alias and CLR name ---------------------------

    [Theory]
    [InlineData("{int}", "42")]
    [InlineData("{Int32}", "42")]
    [InlineData("{word}", "single")]
    [InlineData("{string}", "anything at all")]
    public void Builtin_parameter_types_resolve(string placeholder, string value)
    {
        Assert.True(Matches($"I have {placeholder} here", $"I have {value} here"));
    }

    // ---- R4: optional text and alternation --------------------------------------------------

    [Fact]
    public void Optional_text_matches_with_and_without()
    {
        Assert.True(Matches("I have a(n) item", "I have a item"));
        Assert.True(Matches("I have a(n) item", "I have an item"));
    }

    [Fact]
    public void Slash_alternation_matches_either_side()
    {
        Assert.True(Matches("I have/had an item", "I have an item"));
        Assert.True(Matches("I have/had an item", "I had an item"));
        Assert.False(Matches("I have/had an item", "I hold an item"));
    }

    // ---- indeterminate: never dead ----------------------------------------------------------

    [Fact]
    public void Unregistered_custom_type_is_indeterminate_not_a_non_match()
    {
        var binding = Compile("the damage is {DamageEntity}");
        Assert.True(binding.IsIndeterminate);
        Assert.Contains("DamageEntity", binding.IndeterminateReason);
    }

    [Fact]
    public void Custom_type_resolves_once_its_fragment_is_supplied()
    {
        // What the Roslyn pass will do: supply the enum's member alternation.
        var withEnum = new BindingMatcher(new Dictionary<string, string>(DefaultCucumberExpressionParameterTypes.Fragments)
        {
            ["DamageEntity"] = "(?i:Bumper|Door)",
        });
        var binding = withEnum.Compile(new StepDefinitionInfo { Pattern = "the damage is {DamageEntity}" });

        Assert.False(binding.IsIndeterminate);
        Assert.True(BindingMatcher.Matches(binding, "the damage is Bumper"));
        Assert.True(BindingMatcher.Matches(binding, "the damage is door"));   // enums match case-insensitively
        Assert.False(BindingMatcher.Matches(binding, "the damage is Roof"));
    }

    // ---- R9: method-name convention (bare [Given] with no pattern) --------------------------

    private static CompiledBinding CompileConvention(string method, int parameterCount) =>
        Matcher.Compile(new StepDefinitionInfo { Pattern = "", MethodName = method, ParameterCount = parameterCount, Type = StepDefinitionType.Given });

    [Fact]
    public void Bare_binding_with_no_params_binds_by_its_pascal_case_name()
    {
        var b = CompileConvention("TheOrderIsCreated", parameterCount: 0);
        Assert.False(b.IsIndeterminate);
        Assert.True(BindingMatcher.Matches(b, "the order is created"));
        Assert.True(BindingMatcher.Matches(b, "The Order Is Created"));   // convention is case-insensitive
        Assert.True(BindingMatcher.Matches(b, "the order is created."));  // tolerates one trailing period
        Assert.False(BindingMatcher.Matches(b, "the order is created twice"));
    }

    [Fact]
    public void Bare_binding_strips_a_leading_keyword_word_from_the_name()
    {
        var b = CompileConvention("GivenTheUserIsLoggedIn", parameterCount: 0);
        Assert.True(BindingMatcher.Matches(b, "the user is logged in"));
    }

    [Fact]
    public void Bare_binding_with_parameters_can_never_bind_and_is_unused_not_indeterminate()
    {
        // Reqnroll never binds a parameterised method under bare convention. That is a confident
        // verdict (it matches nothing), so it must be evaluable-and-unused, not hidden as indeterminate.
        var b = CompileConvention("TheOrderIsCreated", parameterCount: 1);
        Assert.False(b.IsIndeterminate);
        Assert.False(BindingMatcher.Matches(b, "the order is created"));
    }

    [Fact]
    public void An_unresolvable_interpolated_pattern_is_indeterminate_not_a_false_convention_binding()
    {
        // [Given($@"^…{Const}…$")]: discovery can't read the interpolated pattern, so it sets
        // UnresolvablePattern. That must become indeterminate (held out of the unused list), NOT be
        // mis-read as an empty method-name-convention pattern — the real the sample suite false-positive (#5/#6).
        var b = Matcher.Compile(new StepDefinitionInfo
        {
            Pattern = "",
            UnresolvablePattern = true,
            MethodName = "GetLanguageTranslations",
            ParameterCount = 1,
            Type = StepDefinitionType.Given,
        });
        Assert.True(b.IsIndeterminate);
        Assert.False(BindingMatcher.Matches(b, "the user gets the language translations for 'en-US' language"));
    }

    [Fact]
    public void A_genuinely_bare_binding_is_still_convention_not_indeterminate()
    {
        // Guard the distinction: no pattern AND not flagged unresolvable = real convention binding.
        var b = Matcher.Compile(new StepDefinitionInfo
        {
            Pattern = "",
            UnresolvablePattern = false,
            MethodName = "TheOrderIsCreated",
            ParameterCount = 0,
            Type = StepDefinitionType.Given,
        });
        Assert.False(b.IsIndeterminate);
        Assert.True(BindingMatcher.Matches(b, "the order is created"));
    }

    [Fact]
    public void Invalid_regex_is_indeterminate_not_dead()
    {
        var binding = Compile("^user does [unclosed$");
        Assert.True(binding.IsIndeterminate);
    }

    [Fact]
    public void Indeterminate_binding_never_matches_anything()
    {
        Assert.False(BindingMatcher.Matches(Compile("the damage is {DamageEntity}"), "the damage is Bumper"));
    }
}
