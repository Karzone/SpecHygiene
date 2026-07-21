# SpecHygiene

Static hygiene checks for **Reqnroll / SpecFlow** BDD solutions. Point it at a folder and it finds the dead weight — no network, no AI, pure static analysis.

## What it checks

| Check | What it finds |
|-------|---------------|
| **Unused code** | Dead C# — methods, classes, and interfaces with no references, using Roslyn's semantic model (symbol-accurate, not text matching). |
| **Unused step definitions** | Reqnroll/SpecFlow `[Given]`/`[When]`/`[Then]` bindings that **no scenario uses** — matched with the real Cucumber-expression / regex semantics the runtime uses, so no false "unused". |
| **Data errors** | Feature-file problems: undefined `<placeholders>`, a `Scenario` that has an `Examples:` table (should be a `Scenario Outline`), malformed data tables, unresolved `@DataSource` CSVs. |
| **Duplicate scenarios** | Exact, containment (superset/subset), and near-duplicate scenarios — deterministic step-fingerprint matching, value-sensitive. |

## Quick start

```bash
# Build
dotnet build -c Release

# Run every check against a solution folder
dotnet run --project src/SpecHygiene -- /path/to/your/solution

# Or a single check
dotnet run --project src/SpecHygiene -- /path/to/your/solution --unused-steps
```

### Usage

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

Configuration defaults live in `src/SpecHygiene/appsettings.json` and can be
overridden there (paths, file patterns, exclusion lists, thresholds). Command-line
flags take precedence for the scan path and which checks run.

## Requirements

- .NET 8 SDK or later. Cross-platform (Windows, macOS, Linux).

## Development

```bash
dotnet build          # build the solution
dotnet test           # run the test suite
```

Layout:

```
src/SpecHygiene         # the CLI + analysis engine
tests/SpecHygiene.Tests # xUnit tests, incl. a matcher eval corpus
```

The matcher tests include an **eval corpus** — a set of binding/step cases whose
expected results are pinned to real Reqnroll runtime behaviour, so changes to the
step-matching logic are gated against ground truth rather than intuition.

## How it works

- **Unused step definitions** parse your `*Steps.cs` via Roslyn to discover bindings
  (honouring `[Binding]` inheritance and bare method-name-convention bindings), then
  match every feature-file step against them using the same expression semantics
  Reqnroll uses at runtime — Cucumber expressions, regex, optional text `(s)`,
  alternation `a/b`, and parameter types. A step that binds is "used"; a binding no
  step reaches is reported as unused.
- **Unused code** builds a Roslyn compilation per project and walks symbol references,
  so a private method that looks called by name but is actually a different overload
  is judged correctly. Test methods, hooks, controllers, and serialization entry
  points are excluded by attribute so they aren't false-flagged.
- **Data errors** and **duplicates** come from a single cheap feature-file parse pass.

## License

MIT — see [LICENSE](LICENSE).
