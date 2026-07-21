# SpecHygiene

**Static hygiene checks for [Reqnroll](https://reqnroll.net/) / SpecFlow BDD solutions.**

Point it at your solution folder and it finds the dead weight — unused code, step
definitions no scenario uses, broken feature-file data, and duplicate scenarios.
Pure static analysis: **no network, no AI, no test run required.**

---

## What it checks

| Check | What it finds |
|-------|---------------|
| **Unused code** | Dead C# — methods, classes, and interfaces with no references, using Roslyn's semantic model (symbol-accurate, not text matching). |
| **Unused step definitions** | `[Given]`/`[When]`/`[Then]` bindings that **no scenario uses**, matched with the same Cucumber-expression / regex semantics Reqnroll uses at runtime — so no false "unused". |
| **Data errors** | Feature-file problems: undefined `<placeholders>`, a `Scenario` that has an `Examples:` table (should be a `Scenario Outline`), malformed data tables, unresolved `@DataSource` CSVs. |
| **Duplicate scenarios** | Exact, containment (superset/subset), and near-duplicate scenarios — deterministic, value-sensitive step-fingerprint matching. |

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later.
- Cross-platform: Windows, macOS, Linux.

## Install & build

```bash
git clone https://github.com/Karzone/SpecHygiene.git
cd SpecHygiene
dotnet build -c Release
```

## Usage

Run every check against a solution or folder:

```bash
dotnet run --project src/SpecHygiene -- /path/to/your/solution
```

Run a single check:

```bash
dotnet run --project src/SpecHygiene -- /path/to/your/solution --unused-steps
```

Combine checks and choose an output folder:

```bash
dotnet run --project src/SpecHygiene -- /path/to/your/solution --unused-code --data-errors --out ./hygiene
```

### Command reference

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

> **Tip:** after `dotnet build`, you can run the compiled binary directly:
> `./src/SpecHygiene/bin/Release/net8.0/spechygiene <path>`

## Output

Every run writes two files to the output directory (default `./reports`):

- **`spechygiene-report.html`** — a self-contained HTML report (open it in any
  browser; no assets, no internet needed). Summary cards per check plus tables of
  every finding with file/line locations. Light- and dark-mode aware.
- **`summary.txt`** — a plain-text summary of the counts, handy for CI logs.

The console prints the same summary as it runs.

## Configuration

Defaults live in [`src/SpecHygiene/appsettings.json`](src/SpecHygiene/appsettings.json)
and can be tuned — scan paths, feature-file patterns, step-definition file globs,
exclusion lists, thresholds, and which checks are enabled. Command-line flags take
precedence for the scan path and which checks run. A minimal example:

```json
{
  "Analysis": { "FeatureFilePattern": "*.feature" },
  "CoverageAnalysis": { "Enabled": true, "UseSyntacticDiscovery": true },
  "UnusedCodeAnalysis": { "Enabled": true, "IncludePublicMembers": false }
}
```

> `UnusedCodeAnalysis.IncludePublicMembers` is **off** by default: in solutions with
> DI, controllers, serialization, or reflection, public members are often reached by
> call paths no static analyzer can see, so flagging them is noisy. Private + internal
> members are assembly-scoped — that's where dead-code detection is sound. Turn it on
> for a stricter (noisier) sweep.

## How it works

- **Unused step definitions** parse your `*Steps.cs` with Roslyn to discover bindings
  (honouring `[Binding]` inheritance and bare method-name-convention bindings), then
  match every feature-file step against them using the same expression semantics
  Reqnroll uses at runtime — Cucumber expressions, regex, optional text `(s)`,
  alternation `a/b`, and parameter types. A step that binds is "used"; a binding no
  step reaches is reported as unused.
- **Unused code** builds a Roslyn compilation and walks symbol references, so an
  overload that merely *looks* called by name is judged correctly. Test methods,
  hooks, controllers, and serialization entry points are excluded by attribute so
  they aren't false-flagged.
- **Data errors** and **duplicates** come from a single cheap feature-file parse pass.

## Development

```bash
dotnet build     # build the solution
dotnet test      # run the test suite (217 tests)
```

Layout:

```
src/SpecHygiene         # the CLI + analysis engine
tests/SpecHygiene.Tests # xUnit tests, incl. a matcher eval corpus
```

The matcher tests include an **eval corpus** — binding/step cases whose expected
results are pinned to real Reqnroll runtime behaviour, so changes to the matching
logic are gated against ground truth rather than intuition.

## License

MIT — see [LICENSE](LICENSE).
