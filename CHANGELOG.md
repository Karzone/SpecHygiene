# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/).

## [1.1.0] — 2026-07-22

### Fixed
- **Coverage false positives.** `CoverageAnalysis.IncludeIgnoredScenarios` now defaults to **true**: a step used only in an `@ignore` / `@wip` scenario is no longer reported as unused — the temporarily-disabled test still needs it. On a real suite this cut false "unused step definitions" from **175 → 7**.

### Changed
- **Data errors** are now rich, collapsible detail cards. Each category (e.g. *Scenario Outlines defined as Scenarios*, *Undefined placeholders — missing values*) folds independently so hundreds of issues stay scannable, and every card shows the specifics: the missing `<placeholders>`, the affected step text, and the Examples columns. The issue line colour-codes what a scenario **is** (red) vs what it **should be** (green), and the scenario title reads as a clear heading.

### Removed
- **Duplicate-scenarios check.** Duplicate detection is fuzzier and its tuning is solution-specific; SpecHygiene now focuses on the three universally-applicable checks — unused code, unused step definitions, and data errors. The `--duplicates` flag, its report section, and the dashboard card have been removed.

## [1.0.3] — 2026-07-22

### Changed
- **Duplicate detection now defaults to whole-scenario comparison.** The previous default counted how often each *step* was reused across scenarios — but step reuse is normal, intended BDD, not a duplicate. The check now finds duplicate / near-duplicate **scenarios** (exact, containment, high-overlap), which is the actionable signal, and reports each match's type, overlap %, and `file:line`. The misleading step-reuse table has been removed from the report.

## [1.0.2] — 2026-07-22

### Fixed
- **Unused code — WPF/XAML false positives.** Event handlers and other XAML-wired members (bindings, converters, control types, `x:Name`) are no longer reported as dead code — XAML is now scanned as a reference source. On a real WPF-backed solution this cut false positives from **69 → 10**. Extraction is position-scoped (attribute values + `{markup extensions}`), so a genuinely-dead method that merely shares a name with a XAML attribute is still reported.

### Added
- **Duplicate steps report** now lists the `file:line` (and scenario) of every occurrence, so findings are directly actionable.

## [1.0.1] — 2026-07-21

### Changed
- Upgraded the Roslyn analyzer (`Microsoft.CodeAnalysis.CSharp`) to 4.14.0 for full **C# 14 / .NET 10** source parsing. The tool already ran on .NET 8/9/10 (a `net8.0` tool rolls forward); this ensures the newest language syntax in .NET 10 codebases is parsed cleanly.

## [1.0.0] — 2026-07-21

First release.

### Checks
- **Unused code** — Roslyn semantic dead-code detection for methods, classes, and interfaces (symbol-accurate, not text matching).
- **Unused step definitions** — Reqnroll / SpecFlow bindings that no scenario uses, matched with the same Cucumber-expression / regex semantics Reqnroll uses at runtime (honouring `[Binding]` inheritance and bare method-name-convention bindings).
- **Data errors** — undefined `<placeholders>`, a `Scenario` carrying an `Examples:` table, malformed data tables, and unresolved `@DataSource` CSVs.
- **Duplicate scenarios** — exact, containment (superset/subset), and near-duplicate detection via deterministic, value-sensitive step fingerprints.

### Tooling
- Self-contained HTML report — dashboard, collapsible sections, light/dark aware, no JavaScript or external assets — plus a plain-text summary.
- Cross-platform .NET 8 CLI, also distributed as a `dotnet tool` (`dotnet tool install -g SpecHygiene`).
- 217 tests, including a matcher eval corpus whose expected results are pinned to real Reqnroll runtime behaviour.

[1.1.0]: https://github.com/Karzone/SpecHygiene/releases/tag/v1.1.0
[1.0.3]: https://github.com/Karzone/SpecHygiene/releases/tag/v1.0.3
[1.0.2]: https://github.com/Karzone/SpecHygiene/releases/tag/v1.0.2
[1.0.1]: https://github.com/Karzone/SpecHygiene/releases/tag/v1.0.1
[1.0.0]: https://github.com/Karzone/SpecHygiene/releases/tag/v1.0.0
