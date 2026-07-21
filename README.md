<h1 align="center">SpecHygiene</h1>

<p align="center">
  <strong>Static hygiene checks for <a href="https://reqnroll.net/">Reqnroll</a> / SpecFlow BDD solutions.</strong><br>
  Find the dead weight — unused code, unused step definitions, broken feature data, and duplicate scenarios.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 8">
  <img src="https://img.shields.io/badge/tests-217%20passing-brightgreen" alt="217 tests passing">
  <img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License: MIT">
  <img src="https://img.shields.io/badge/AI-none-lightgrey" alt="No AI">
  <img src="https://img.shields.io/badge/network-none-lightgrey" alt="No network">
</p>

---

Point it at your solution folder and it reports what's rotting: C# no one calls, step
definitions no scenario uses, feature files with broken data, and copy-pasted scenarios.
**Pure static analysis — no network, no AI, no test run required.**

## See it in action

▶ **[View a live sample report](https://htmlpreview.github.io/?https://github.com/Karzone/SpecHygiene/blob/main/docs/sample-report.html)** — generated from the tiny demo in [`samples/demo`](samples/demo), it shows a finding in each of the four checks.

## What it checks

| Check | What it finds |
|-------|---------------|
| **Unused code** | Dead C# — methods, classes, and interfaces with no references, using Roslyn's semantic model (symbol-accurate, not text matching). |
| **Unused step definitions** | `[Given]`/`[When]`/`[Then]` bindings that **no scenario uses**, matched with the same Cucumber-expression / regex semantics Reqnroll uses at runtime — so no false "unused". |
| **Data errors** | Feature-file problems: undefined `<placeholders>`, a `Scenario` that has an `Examples:` table (should be a `Scenario Outline`), malformed data tables, unresolved `@DataSource` CSVs. |
| **Duplicate scenarios** | Exact, containment (superset/subset), and near-duplicate scenarios — deterministic, value-sensitive step-fingerprint matching. |

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later — cross-platform (Windows, macOS, Linux).

## Quick start

```bash
git clone https://github.com/Karzone/SpecHygiene.git
cd SpecHygiene
dotnet build -c Release

# Run every check against your solution or folder
dotnet run --project src/SpecHygiene -- /path/to/your/solution
```

Try it on the bundled demo first:

```bash
dotnet run --project src/SpecHygiene -- ./samples/demo
```

## Usage

```
spechygiene <path> [checks] [--out <dir>]

Checks (default: all):
  --unused-code     Roslyn dead-code (unused methods/classes/interfaces)
  --unused-steps    Step definitions no scenario uses
  --data-errors     Feature-file data errors (undefined placeholders, etc.)
  --duplicates      Duplicate / near-duplicate scenarios
  --all             Run every check (default when none specified)

Options:
  --out <dir>       Output directory (default ./reports)
  -h, --help        Show this help
```

Examples:

```bash
# Just the unused-step-definition check
dotnet run --project src/SpecHygiene -- /path/to/solution --unused-steps

# Two checks, custom output folder
dotnet run --project src/SpecHygiene -- /path/to/solution --unused-code --data-errors --out ./hygiene
```

> **Tip:** after `dotnet build`, run the compiled binary directly:
> `./src/SpecHygiene/bin/Release/net8.0/spechygiene <path>`

## The report

Every run writes to the output directory (default `./reports`):

| File | What it is |
|------|-----------|
| **`spechygiene-report.html`** | A self-contained HTML report — summary cards per check plus a table of every finding with file/line locations. Light- & dark-mode aware. |
| **`summary.txt`** | A plain-text summary of the counts — handy for CI logs. |

The console prints the same summary as it runs.

**How the HTML is generated** — it's produced automatically on every run (unless you set
`Output.GenerateHtml` to `false` in `appsettings.json`). It's a **single file with all CSS
inlined and no JavaScript or external assets**, so it works completely offline and is safe to
email or archive.

**How to view it** — just open the file in any browser:

```bash
# macOS
open reports/spechygiene-report.html
# Windows
start reports\spechygiene-report.html
# Linux
xdg-open reports/spechygiene-report.html
```

Or see the **[live sample report](https://htmlpreview.github.io/?https://github.com/Karzone/SpecHygiene/blob/main/docs/sample-report.html)** rendered straight from this repo.

## Configuration

Defaults live in [`src/SpecHygiene/appsettings.json`](src/SpecHygiene/appsettings.json) and can
be tuned — scan paths, feature-file patterns, step-definition globs, exclusion lists, thresholds,
and which checks are enabled. Command-line flags win for the scan path and which checks run.

```json
{
  "Analysis": { "FeatureFilePattern": "*.feature" },
  "CoverageAnalysis": { "Enabled": true, "UseSyntacticDiscovery": true },
  "UnusedCodeAnalysis": { "Enabled": true, "IncludePublicMembers": false }
}
```

> `UnusedCodeAnalysis.IncludePublicMembers` is **off** by default: in solutions with DI,
> controllers, serialization, or reflection, public members are often reached by call paths no
> static analyzer can see, so flagging them is noisy. Private + internal members are
> assembly-scoped — that's where dead-code detection is sound. Turn it on for a stricter sweep.

## How it works

- **Unused step definitions** — parse your `*Steps.cs` with Roslyn to discover bindings (honouring
  `[Binding]` inheritance and bare method-name-convention bindings), then match every feature-file
  step against them using the same expression semantics Reqnroll uses at runtime — Cucumber
  expressions, regex, optional text `(s)`, alternation `a/b`, and parameter types. A step that
  binds is "used"; a binding no step reaches is reported as unused.
- **Unused code** — build a Roslyn compilation and walk symbol references, so an overload that
  merely *looks* called by name is judged correctly. Test methods, hooks, controllers, and
  serialization entry points are excluded by attribute so they aren't false-flagged.
- **Data errors** and **duplicates** come from a single cheap feature-file parse pass.

## Development

```bash
dotnet build     # build the solution
dotnet test      # run the test suite (217 tests)
```

```
src/SpecHygiene         # the CLI + analysis engine
tests/SpecHygiene.Tests # xUnit tests, incl. a matcher eval corpus
samples/demo            # a tiny solution that triggers every check
```

The matcher tests include an **eval corpus** — binding/step cases whose expected results are
pinned to real Reqnroll runtime behaviour, so changes to the matching logic are gated against
ground truth rather than intuition.

## Contributing

Issues and pull requests are welcome. Please run `dotnet test` before opening a PR.

## License

[MIT](LICENSE) © Karthik Kalaiyarasu
