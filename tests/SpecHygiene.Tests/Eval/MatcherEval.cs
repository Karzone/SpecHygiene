using System.Text;
using SpecHygiene.Analysis.Reqnroll;
using SpecHygiene.Models;
using Xunit;
using Xunit.Abstractions;

namespace SpecHygiene.Tests.Eval;

/// <summary>
/// Head-to-head eval: the old eight-strategy ladder vs the ported Reqnroll matcher, over a corpus of
/// cases whose ground truth is Reqnroll's actual behaviour.
/// <para>
/// Ground truth is NOT my judgement. Every case is either (a) a production regression dead-step-finder
/// names in its source comments, confirmed against a real Reqnroll+NUnit run, or (b) a rule its
/// empirical verification project established. Reqnroll 3.3.3 — the version the sample suite runs.
/// </para>
/// <para>
/// Two verdict errors, and they are not equally bad. A false ALIVE under-reports (safe, hides dead
/// code). A false DEAD says "delete this" about live code — the one this tool must never do.
/// </para>
/// </summary>
public sealed class MatcherEval(ITestOutputHelper output)
{
    /// <param name="ShouldMatch">What real Reqnroll does — the ground truth.</param>
    private sealed record Case(
        string Id,
        string Pattern,
        string StepText,
        bool ShouldMatch,
        string Provenance);

    private static readonly Case[] Corpus =
    [
        // ---- R1: full match. The ladder's Strategy 3 matched unanchored. -------------------
        new("R1-substring", "I do something", "I do something extra", false,
            "MatchingLogic.cs: 'a real, confirmed false-positive source'"),
        new("R1-exact", "I do something", "I do something", true,
            "baseline sanity"),
        new("R1-prefix", "user submits the estimate", "user submits the estimate on the Order", false,
            "R1 corollary"),

        // ---- R8: Reqnroll wraps ^/$ only if TEXTUALLY absent, so ^A|B$ stays unanchored. ---
        new("R8-alternation-anchors", "^user updates and completes|cancels manual task$",
            "user updates and completes manual task for the order", true,
            "OrderTaskSteps.ThenUserUpdatesManualTask, Acme.API.Motor - confirmed ALIVE by a real Reqnroll+NUnit run"),

        // ---- R4: unescaped (text) is OPTIONAL TEXT, not a literal. -------------------------
        new("R4-optional-present", "I have a(n) item", "I have an item", true,
            "PatternCompiler.cs ProductionRegression: Acme.API.DataSet / Acme.API.Estimate"),
        new("R4-optional-absent", "I have a(n) item", "I have a item", true,
            "PatternCompiler.cs ProductionRegression"),
        new("R4-slash-alternation", "I have/had an item", "I had an item", true,
            "Cucumber '/' alternation"),
        new("R4-slash-negative", "I have/had an item", "I hold an item", false,
            "alternation must not over-match"),

        // ---- R5: every numeric type registers under BOTH C# alias and CLR name. ------------
        new("R5-clr-name", "{Int32} suppliers should be returned", "5 suppliers should be returned", true,
            "AllocationSteps, Acme.API.OrderProcessor - missing the CLR-name half caused a false 'undefined step'"),
        new("R5-alias", "{int} suppliers should be returned", "5 suppliers should be returned", true,
            "built-in alias"),
        new("R5-int-negative", "{int} suppliers should be returned", "many suppliers should be returned", false,
            "{int} must not match non-numeric"),

        // ---- raw regex still works through the classifier ----------------------------------
        new("CLS-raw-regex", "^user completes (.*) task$", "user completes the allocation task", true,
            "raw-regex path"),
        new("CLS-raw-regex-negative", "^user completes (.*) task$", "user abandons the allocation task", false,
            "raw-regex path must still discriminate"),
    ];

    /// <summary>
    /// The matcher half of the &lt;ShouldOrShouldNot&gt; regression: once the corpus supplies the real
    /// Examples value ("SHOULD"), the binding's hardcoded alternation must match it.
    /// <para>
    /// ANCHORED deliberately. The regression comment quotes the pattern fragment "(SHOULD|should NOT)"
    /// but not the whole attribute; an UNANCHORED pattern would route to the Cucumber grammar, where
    /// "(...)" is optional text and "|" is a literal — so it could never bind "SHOULD", and Reqnroll
    /// would agree. A binding like that never fires, so it cannot exist in a passing suite: the real
    /// one is anchored, i.e. a raw regex. The corpus-side half of this regression (using the real
    /// Examples value rather than a guess) is covered by StepCorpusParsingTests.
    /// </para>
    /// </summary>
    private static readonly Case OutlineCase = new(
        "R3-outline-real-value",
        "^the supplier from context (SHOULD|should NOT) be returned$",
        "the supplier from context SHOULD be returned", true,
        "dead-step-finder ScenarioOutlineExpander.cs: <ShouldOrShouldNot> - binding reported dead despite being genuinely invoked");

    private static bool LegacyMatches(Case c)
    {
        var def = new StepDefinitionInfo
        {
            Pattern = c.Pattern,
            // The ladder read RegexPattern for its Strategy 2/5. The old parser's ConvertToRegex is
            // gone, so use the pattern itself — the ladder's Strategies 1/3/4/6/7/8 all read Pattern
            // anyway, and this is the most generous possible reading of the old behaviour.
            RegexPattern = c.Pattern,
            Type = StepDefinitionType.Given,
        };
        try { return new LegacyMatchLadder().TryMatchStepToDefinition(c.StepText, def); }
        catch { return false; }
    }

    private static bool NewMatches(Case c)
    {
        var matcher = new BindingMatcher();
        var binding = matcher.Compile(new StepDefinitionInfo { Pattern = c.Pattern, Type = StepDefinitionType.Given });
        return !binding.IsIndeterminate && BindingMatcher.Matches(binding, c.StepText);
    }

    [Fact]
    public void New_matcher_scores_strictly_better_than_the_old_ladder()
    {
        var cases = Corpus.Append(OutlineCase).ToArray();

        int oldFalseAlive = 0, oldFalseDead = 0, newFalseAlive = 0, newFalseDead = 0;
        var rows = new StringBuilder();
        rows.AppendLine();
        rows.AppendLine("| case | expected | old | new | verdict |");
        rows.AppendLine("|---|---|---|---|---|");

        foreach (var c in cases)
        {
            var o = LegacyMatches(c);
            var n = NewMatches(c);

            if (o != c.ShouldMatch) { if (o) oldFalseAlive++; else oldFalseDead++; }
            if (n != c.ShouldMatch) { if (n) newFalseAlive++; else newFalseDead++; }

            var verdict = (o == c.ShouldMatch, n == c.ShouldMatch) switch
            {
                (false, true) => "FIXED",
                (true, false) => "REGRESSED",
                (true, true)  => "both ok",
                (false, false) => "both wrong",
            };
            rows.AppendLine($"| {c.Id} | {Word(c.ShouldMatch)} | {Word(o)} | {Word(n)} | {verdict} |");
        }

        var oldWrong = oldFalseAlive + oldFalseDead;
        var newWrong = newFalseAlive + newFalseDead;

        rows.AppendLine();
        rows.AppendLine($"cases            : {cases.Length}");
        rows.AppendLine($"old ladder wrong : {oldWrong}  (false-alive {oldFalseAlive}, false-DEAD {oldFalseDead})");
        rows.AppendLine($"new matcher wrong: {newWrong}  (false-alive {newFalseAlive}, false-DEAD {newFalseDead})");
        rows.AppendLine($"accuracy         : {Pct(cases.Length - oldWrong, cases.Length)} -> {Pct(cases.Length - newWrong, cases.Length)}");
        output.WriteLine(rows.ToString());

        // The bar: the new matcher must be perfect on cases whose ground truth is a real Reqnroll run,
        // and must never regress one the ladder got right.
        Assert.True(newWrong < oldWrong, $"new matcher should beat the ladder{rows}");
        Assert.Equal(0, newFalseDead);
        Assert.Equal(0, newWrong);
    }

    [Fact]
    public void No_case_the_old_ladder_got_right_is_now_wrong()
    {
        var regressions = Corpus.Append(OutlineCase)
            .Where(c => LegacyMatches(c) == c.ShouldMatch && NewMatches(c) != c.ShouldMatch)
            .Select(c => c.Id)
            .ToList();

        Assert.True(regressions.Count == 0, "regressed: " + string.Join(", ", regressions));
    }

    private static string Word(bool b) => b ? "match" : "no";
    private static string Pct(int n, int d) => $"{100.0 * n / d:F0}%";
}
