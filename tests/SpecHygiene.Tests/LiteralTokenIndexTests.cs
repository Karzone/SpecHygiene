using SpecHygiene.Analysis.Reqnroll;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// The index is a speed optimisation, so its one hard requirement is EQUIVALENCE: for any step, the
/// set of bindings it says to test-and-match must equal what a brute-force scan of every binding would
/// match. The danger is a false SKIP (a binding that would match but the index omits = a false dead),
/// so the corpus below is deliberately adversarial: literals glued to params, optional text,
/// alternation, the ^A|B$ quirk, case differences, and substring traps.
/// </summary>
public sealed class LiteralTokenIndexTests
{
    private static readonly BindingMatcher Matcher = new();

    private static CompiledBinding B(string pattern, StepDefinitionType type = StepDefinitionType.Given) =>
        Matcher.Compile(new StepDefinitionInfo { Pattern = pattern, Type = type, MethodName = "M" });

    private static CompiledBinding Convention(string method, int paramCount) =>
        Matcher.Compile(new StepDefinitionInfo { Pattern = "", MethodName = method, ParameterCount = paramCount, Type = StepDefinitionType.Given });

    // The bindings under test — a mix that exercises every extraction branch.
    private static readonly CompiledBinding[] Bindings =
    [
        B("I wait {int} seconds"),                          // cucumber, two clean literals
        B("the supplier is returned"),                      // all literal (still parametric? no params -> literal; keep as control)
        B("I have a(n) item"),                              // optional text — "a"/"n" not required
        B("I have/had an item"),                            // alternation — neither have/had required
        B("^user updates and completes|cancels task$"),     // the ^A|B$ quirk (raw, top-level |)
        B("^user (.*) completes task$"),                    // raw with group; "user","completes","task" required
        B("weigh {int}kgs total"),                          // literal "kgs" GLUED to param — must not be a required token
        B("prefix{word}"),                                  // "prefix" glued to param
        B("the order is Created"),                          // case: literal "Created" (capital)
        Convention("TheReportIsReady", 0),                  // R9 convention
        Convention("TheThingHappens", 2),                   // R9 params -> never binds
    ];

    // Steps chosen to probe the traps.
    private static readonly string[] Steps =
    [
        "I wait 5 seconds",
        "I wait many seconds",              // {int} rejects "many" — must still be TESTED, just not matched
        "the supplier is returned",
        "I have a item",
        "I have an item",
        "I have had an item",
        "user updates and completes task for the order",   // the quirk — binds via ^...completes branch
        "user quickly completes task",
        "weigh 5kgs total",                 // glued literal
        "prefixvalue",                      // glued literal, no space
        "the order is Created",
        "the order is created",             // case mismatch vs "Created"
        "the report is ready",
        "the thing happens",
        "something entirely unrelated",
        "multitask completes",              // substring trap: contains "task" only inside "multitask"
    ];

    private static List<string> BruteForce(string step) =>
        Bindings.Where(b => BindingMatcher.Matches(b, step))
                .Select(b => b.Definition.Pattern + "|" + b.Definition.MethodName)
                .OrderBy(x => x).ToList();

    private static List<string> ViaIndex(LiteralTokenIndex index, string step)
    {
        var tokens = LiteralTokenIndex.Tokenize(step);
        return index.Candidates(tokens)
                    .Where(b => BindingMatcher.Matches(b, step))
                    .Select(b => b.Definition.Pattern + "|" + b.Definition.MethodName)
                    .OrderBy(x => x).ToList();
    }

    [Fact]
    public void Index_matching_is_identical_to_brute_force_for_every_step()
    {
        var index = new LiteralTokenIndex(Bindings);
        foreach (var step in Steps)
        {
            var brute = BruteForce(step);
            var viaIndex = ViaIndex(index, step);
            Assert.True(brute.SequenceEqual(viaIndex),
                $"MISMATCH for step '{step}'\n  brute: [{string.Join(", ", brute)}]\n  index: [{string.Join(", ", viaIndex)}]");
        }
    }

    [Fact]
    public void A_literal_glued_to_a_parameter_is_not_treated_as_a_required_token()
    {
        // "weigh {int}kgs total" — if "kgs" were required as a token, the step "weigh 5kgs total"
        // (token "5kgs", not "kgs") would be falsely skipped. Equivalence must hold.
        var index = new LiteralTokenIndex(Bindings);
        Assert.Equal(BruteForce("weigh 5kgs total"), ViaIndex(index, "weigh 5kgs total"));
    }

    [Fact]
    public void The_substring_trap_does_not_cause_a_false_match_or_skip()
    {
        // "multitask completes": "task" appears only inside "multitask". A binding requiring the token
        // "task" must not be spuriously matched, and one that genuinely matches must not be skipped.
        var index = new LiteralTokenIndex(Bindings);
        Assert.Equal(BruteForce("multitask completes"), ViaIndex(index, "multitask completes"));
    }

    [Fact]
    public void Alternation_and_quirk_bindings_go_to_always_scan_not_a_false_key()
    {
        // Bindings with top-level | must be always-scanned, so the ^A|B$ quirk step still binds.
        var index = new LiteralTokenIndex(Bindings);
        var step = "user updates and completes task for the order";
        Assert.Equal(BruteForce(step), ViaIndex(index, step));
        Assert.True(index.AlwaysScanCount >= 1);   // the |-bearing binding is not indexed
    }

    [Fact]
    public void A_binding_that_survives_the_filter_is_still_rejected_by_the_matcher_when_it_should_not_match()
    {
        // "I wait many seconds": passes the token filter for "I wait {int} seconds" (has wait, seconds)
        // but {int} rejects "many". The index must include it as a candidate; Matches must reject it.
        var index = new LiteralTokenIndex(Bindings);
        Assert.Equal(BruteForce("I wait many seconds"), ViaIndex(index, "I wait many seconds"));
    }
}
