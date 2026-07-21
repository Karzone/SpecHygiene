using SpecHygiene.Analysis;
using SpecHygiene.Models;
using Xunit;

namespace SpecHygiene.Tests;

/// <summary>
/// Pins the feature-corpus rules adopted from dead-step-finder: Examples: expansion under a plain
/// "Scenario:" (R3), the Feature narrative guard + case-sensitive keywords (R10), and the "* "
/// asterisk step the ported guard doesn't model.
/// <para>
/// The parser is private, so each rule is observed through the public report: a binding that only a
/// given line could match is USED iff that line entered the corpus as a step. For the guard rules
/// that inverts — the binding must come back UNUSED, proving the prose was never treated as a step.
/// </para>
/// </summary>
public sealed class StepCorpusParsingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdd-corpus-" + Guid.NewGuid().ToString("N"));
    private readonly string _features;
    private readonly string _stepsDir;

    public StepCorpusParsingTests()
    {
        _features = Path.Combine(_root, "Features");
        _stepsDir = Path.Combine(_root, "Steps");
        Directory.CreateDirectory(_features);
        Directory.CreateDirectory(_stepsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }

    private void Feature(string name, string content) =>
        File.WriteAllText(Path.Combine(_features, name + ".feature"), content.ReplaceLineEndings("\n"));

    private void Binding(string pattern) =>
        File.WriteAllText(Path.Combine(_stepsDir, "Steps.cs"), $$"""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"{{pattern}}")]
                public void TheStep() { }
            }
            """.ReplaceLineEndings("\n"));

    private StepDefinitionCoverageReport Run(string? knownIssuesCsv = null) =>
        new StepDefinitionCoverageAnalyzer(knownIssuesCsvPath: knownIssuesCsv)
            .AnalyzeCoverage(new[] { _stepsDir }, new[] { _features });

    private string WriteKnownIssues(string csv)
    {
        var path = Path.Combine(_root, "known-issues.csv");
        File.WriteAllText(path, csv.ReplaceLineEndings("\n"));
        return path;
    }

    // ---- known-issues CSV: accepted-dead is still dead, just not re-litigated ----------------

    [Fact]
    public void A_known_issue_stays_unused_but_leaves_the_actionable_list()
    {
        Binding("a dead step");
        Feature("ki", """
            Feature: F

            Scenario: s
                Given something else
            """);

        var csv = WriteKnownIssues("""
            SourceFile,MethodName,Comment
            Steps.cs,TheStep,only usage is commented out, tracked in JIRA-123
            """);

        var report = Run(csv);
        Assert.Equal(1, report.UnusedStepDefinitions);          // the verdict is unchanged
        Assert.Single(report.KnownIssueDefinitions);            // but it is accounted for
        Assert.Empty(report.ActionableUnusedDefinitions);       // and not re-reported as fresh
        Assert.Contains("JIRA-123", report.KnownIssueDefinitions[0].Comment);
    }

    [Fact]
    public void An_unlisted_dead_binding_stays_actionable()
    {
        Binding("a dead step");
        Feature("ki2", """
            Feature: F

            Scenario: s
                Given something else
            """);

        var csv = WriteKnownIssues("""
            SourceFile,MethodName,Comment
            Other.cs,SomethingElse,not this one
            """);

        var report = Run(csv);
        Assert.Empty(report.KnownIssueDefinitions);
        Assert.Single(report.ActionableUnusedDefinitions);
    }

    [Fact]
    public void A_missing_known_issues_file_is_not_an_error()
    {
        Binding("a dead step");
        Feature("ki3", """
            Feature: F

            Scenario: s
                Given something else
            """);

        var report = Run(Path.Combine(_root, "does-not-exist.csv"));
        Assert.Equal(1, report.UnusedStepDefinitions);
        Assert.Single(report.ActionableUnusedDefinitions);   // everything stays actionable
    }

    // ---- R3: Examples: expands under a plain "Scenario:", not just "Scenario Outline:" --------

    [Fact]
    public void Examples_table_expands_under_a_plain_Scenario_keyword()
    {
        // Gherkin expands Examples identically under "Scenario:"; keying on the "Scenario Outline"
        // keyword silently skipped every such block. Only the EXPANDED text can match this binding.
        Binding("the supplier SHOULD be returned");
        Feature("plain", """
            Feature: F

            Scenario: plain scenario with examples
                Given the supplier <Verdict> be returned

            Examples:
                | Verdict |
                | SHOULD  |
            """);

        Assert.Equal(0, Run().UnusedStepDefinitions);
    }

    [Fact]
    public void Examples_table_still_expands_under_Scenario_Outline()
    {
        Binding("the supplier SHOULD be returned");
        Feature("outline", """
            Feature: F

            Scenario Outline: outline with examples
                Given the supplier <Verdict> be returned

            Examples:
                | Verdict |
                | SHOULD  |
            """);

        Assert.Equal(0, Run().UnusedStepDefinitions);
    }

    // ---- R10: narrative prose is not a step, and keywords are case-sensitive -----------------

    [Fact]
    public void Feature_narrative_starting_with_and_is_not_a_step()
    {
        // "and compares against outbound payload" is prose continuing the previous sentence. If it
        // were parsed as a step it would match this binding and mark it used.
        Binding("compares against outbound payload");
        Feature("narrative", """
            Feature: F
                This feature polls search and retrieve
                and compares against outbound payload

            Scenario: s
                Given something else entirely
            """);

        Assert.Equal(1, Run().UnusedStepDefinitions);
    }

    [Fact]
    public void Lowercase_keyword_inside_a_scenario_is_not_a_step()
    {
        // Real Gherkin matches step keywords case-sensitively — lowercase "and" is never a keyword.
        Binding("this lowercase line is not a keyword");
        Feature("case", """
            Feature: F

            Scenario: s
                Given something else entirely
                and this lowercase line is not a keyword
            """);

        Assert.Equal(1, Run().UnusedStepDefinitions);
    }

    [Fact]
    public void Capitalised_And_step_inside_a_scenario_still_counts_as_a_usage()
    {
        // The guard rules REMOVE lines from the corpus, so they carry a false-dead risk if they ever
        // over-remove. Every other guard test asserts something is excluded; this pins the boundary
        // from the other side — a legitimate And step must survive intact.
        Binding("a real And step");
        Feature("andstep", """
            Feature: F

            Scenario: s
                Given something else entirely
                And a real And step
            """);

        Assert.Equal(0, Run().UnusedStepDefinitions);
    }

    // ---- the swap, end to end ---------------------------------------------------------------

    [Fact]
    public void Partial_match_no_longer_marks_a_binding_used()
    {
        // The old ladder reported this binding USED against "I do something extra". Reqnroll requires
        // a full match, so it is genuinely dead. This is the fix that RAISES the dead count.
        Binding("I do something");
        Feature("partial", """
            Feature: F

            Scenario: s
                Given I do something extra
            """);

        Assert.Equal(1, Run().UnusedStepDefinitions);
    }

    [Fact]
    public void Unresolvable_custom_type_is_indeterminate_and_kept_out_of_the_unused_list()
    {
        // Until the Roslyn pass resolves {DamageEntity}, we cannot evaluate this binding. It must not
        // be reported unused — "could not tell" is not "unused".
        Binding("the damage is {DamageEntity}");
        Feature("custom", """
            Feature: F

            Scenario: s
                Given the damage is Bumper
            """);

        var report = Run();
        Assert.Equal(0, report.UnusedStepDefinitions);
        Assert.Single(report.IndeterminateDefinitions);
        Assert.Contains("DamageEntity", report.IndeterminateDefinitions[0].Reason);
    }

    [Fact]
    public void A_parametric_binding_is_used_even_when_a_literal_binding_also_matches_the_step()
    {
        // Reqnroll binds a step to EVERY matching binding. The exact-text fast path keys on the
        // pattern, so a concrete step "I wait 5 seconds" hits only the literal binding — and if the
        // fast path short-circuits there, the parametric binding is never scanned and reports dead
        // despite Reqnroll binding it. A false dead: the direction that gets live code deleted.
        File.WriteAllText(Path.Combine(_stepsDir, "Steps.cs"), """
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"I wait 5 seconds")]
                public void Literal() { }

                [Given(@"I wait {int} seconds")]
                public void Parametric() { }
            }
            """.ReplaceLineEndings("\n"));
        Feature("both", """
            Feature: F

            Scenario: s
                Given I wait 5 seconds
            """);

        var report = Run();
        Assert.Equal(0, report.UnusedStepDefinitions);
    }

    [Fact]
    public void An_enum_declared_in_source_resolves_its_placeholder_end_to_end()
    {
        // The payoff of discovery (R6): the same {DamageEntity} binding that is indeterminate without
        // the enum in scope becomes fully evaluable — and USED — once the enum is discovered from
        // source. This is what shrinks the indeterminate bucket into an actionable dead list.
        File.WriteAllText(Path.Combine(_stepsDir, "Steps.cs"), """
            using Reqnroll;
            namespace X;

            public enum DamageEntity { Bumper, Door }

            [Binding]
            public class S
            {
                [Given(@"the damage is {DamageEntity}")]
                public void TheStep(DamageEntity e) { }
            }
            """.ReplaceLineEndings("\n"));
        Feature("enum", """
            Feature: F

            Scenario: s
                Given the damage is Bumper
            """);

        var report = Run();
        Assert.Equal(1, report.UsedStepDefinitions);
        Assert.Equal(0, report.UnusedStepDefinitions);
        Assert.Empty(report.IndeterminateDefinitions);   // no longer unevaluable
    }

    [Fact]
    public void A_bare_attribute_binding_is_matched_by_method_name_convention_end_to_end()
    {
        // R9 through the real pipeline: discovery finds the bare [Given] (no pattern) and its zero
        // param count; the matcher binds it by the PascalCase method name. Used, not indeterminate.
        File.WriteAllText(Path.Combine(_stepsDir, "Steps.cs"), """
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given]
                public void TheOrderIsCreated() { }
            }
            """.ReplaceLineEndings("\n"));
        Feature("conv", """
            Feature: F

            Scenario: s
                Given the order is created
            """);

        var report = Run();
        Assert.Equal(1, report.UsedStepDefinitions);
        Assert.Equal(0, report.UnusedStepDefinitions);
        Assert.Empty(report.IndeterminateDefinitions);
    }

    [Fact]
    public void A_step_method_outside_a_Binding_class_is_not_reported_as_a_dead_binding()
    {
        // R11 gating: Reqnroll only scans [Binding] classes, so a [Given] on a plain helper is not a
        // binding at all. The regex parser had no notion of [Binding] and would have reported it dead.
        File.WriteAllText(Path.Combine(_stepsDir, "Steps.cs"), """
            using Reqnroll;
            namespace X;

            public class NotABindingClass
            {
                [Given(@"a step nobody registers")]
                public void TheStep() { }
            }
            """.ReplaceLineEndings("\n"));
        Feature("nobinding", """
            Feature: F

            Scenario: s
                Given something else
            """);

        var report = Run();
        Assert.Equal(0, report.TotalStepDefinitions);
        Assert.Equal(0, report.UnusedStepDefinitions);
    }

    [Fact]
    public void Used_unused_and_indeterminate_reconcile_to_the_total()
    {
        File.WriteAllText(Path.Combine(_stepsDir, "Steps.cs"), """
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [Given(@"a used step")]
                public void Used() { }

                [Given(@"a dead step")]
                public void Dead() { }

                [Given(@"the damage is {DamageEntity}")]
                public void Indeterminate() { }
            }
            """.ReplaceLineEndings("\n"));
        Feature("mix", """
            Feature: F

            Scenario: s
                Given a used step
            """);

        var report = Run();
        Assert.Equal(3, report.TotalStepDefinitions);
        Assert.Equal(1, report.UsedStepDefinitions);
        Assert.Equal(1, report.UnusedStepDefinitions);
        Assert.Single(report.IndeterminateDefinitions);
        Assert.Equal(report.TotalStepDefinitions,
            report.UsedStepDefinitions + report.UnusedStepDefinitions + report.IndeterminateDefinitions.Count);
    }

    // ---- R2: keyword compatibility ----------------------------------------------------------

    private void BindingOfKind(string kind, string pattern) =>
        File.WriteAllText(Path.Combine(_stepsDir, "Steps.cs"), $$"""
            using Reqnroll;
            namespace X;

            [Binding]
            public class S
            {
                [{{kind}}(@"{{pattern}}")]
                public void TheStep() { }
            }
            """.ReplaceLineEndings("\n"));

    [Fact]
    public void A_Given_binding_is_not_used_by_a_When_step_of_the_same_text()
    {
        // Reqnroll never binds a [Given] to a When step, however well the text matches. This was the
        // keyword-agnostic gap: the binding was reported alive by a step that cannot call it.
        BindingOfKind("Given", "the task runs");
        Feature("kw", """
            Feature: F

            Scenario: s
                When the task runs
            """);

        Assert.Equal(1, Run().UnusedStepDefinitions);
    }

    [Fact]
    public void A_Given_binding_is_used_by_a_Given_step()
    {
        BindingOfKind("Given", "the task runs");
        Feature("kw2", """
            Feature: F

            Scenario: s
                Given the task runs
            """);

        Assert.Equal(0, Run().UnusedStepDefinitions);
    }

    [Fact]
    public void StepDefinition_binding_matches_any_keyword()
    {
        BindingOfKind("StepDefinition", "the task runs");
        Feature("kw3", """
            Feature: F

            Scenario: s
                Then the task runs
            """);

        Assert.Equal(0, Run().UnusedStepDefinitions);
    }

    [Fact]
    public void An_And_step_inherits_the_preceding_concrete_keyword()
    {
        // "And the task runs" after a When resolves to When — so a [When] binding IS callable, and a
        // [Given] binding is not. Getting the inheritance wrong flips both.
        BindingOfKind("When", "the task runs");
        Feature("kw4", """
            Feature: F

            Scenario: s
                When something starts
                And the task runs
            """);

        Assert.Equal(0, Run().UnusedStepDefinitions);
    }

    [Fact]
    public void An_And_step_does_not_bind_a_keyword_it_did_not_inherit()
    {
        BindingOfKind("Given", "the task runs");
        Feature("kw5", """
            Feature: F

            Scenario: s
                When something starts
                And the task runs
            """);

        Assert.Equal(1, Run().UnusedStepDefinitions);   // resolved keyword is When, not Given
    }

    [Fact]
    public void Keyword_inheritance_resets_at_each_scenario()
    {
        // The second scenario's And must inherit ITS OWN Given, not the previous scenario's When.
        BindingOfKind("Given", "the task runs");
        Feature("kw6", """
            Feature: F

            Scenario: first
                When something starts

            Scenario: second
                Given something else
                And the task runs
            """);

        Assert.Equal(0, Run().UnusedStepDefinitions);
    }

    // ---- asterisk steps must survive (dropping them would manufacture false deads) -----------

    [Fact]
    public void Asterisk_step_still_counts_as_a_usage()
    {
        // The ported guard models Given/When/Then/And/But but not "* ", which is valid Gherkin and
        // which this parser has always accepted. Losing it would turn live bindings dead.
        Binding("an asterisk step");
        Feature("asterisk", """
            Feature: F

            Scenario: s
                * an asterisk step
            """);

        Assert.Equal(0, Run().UnusedStepDefinitions);
    }
}
