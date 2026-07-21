# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/).

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

[1.0.0]: https://github.com/Karzone/SpecHygiene/releases/tag/v1.0.0
