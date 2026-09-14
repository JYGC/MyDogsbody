# Naming compliance — tasks

Work sequentially; each phase's agent must leave the affected project(s) building clean
before the next phase starts.

- [x] 1. `MyDogsbody.Domain` — rename all violating identifiers; grep repo-wide for any
      renamed public symbol's call sites (integrations, Startup, UI.Types, Tests) and update
      them too, even before those projects' own phases run. Build `MyDogsbody.Domain.fsproj`.
- [x] 2. `MyDogsbody.Builders`, `MyDogsbody.Exceptions`, `MyDogsbody.Exceptions.Types`.
      Build each + anything referencing them.
- [x] 3. `MyDogsbody.Database.Models`, `MyDogsbody.Database`, `MyDogsbody.Database.Migrations`
      — includes a new FluentMigrator migration per renamed SQLite column (never edit an
      applied migration). Build all three.
- [x] 4. `MyDogsbody.Integrations.Google` (+ `.Database.Models`), `.Integrations.Documents`,
      `.Integrations.Thunderbird` (+ `.Database.Models`) — includes LiteDB entity property
      renames + `[BsonId]` fix-ups where a renamed property was the implicit primary key.
      Build all.
- [x] 5. `MyDogsbody.Logging` + `MyDogsbody.Logging.Database.Models`. Build.
- [x] 6. `MyDogsbody.Startup`. Build.
- [x] 7. `MyDogsbody.UI.Types`. Build.
- [x] 8. `MyDogsbody.UI.Portal` + WPF host `MyDogsbody` (C#). Build.
- [x] 9. `MyDogsbody.Tests` — recompile against every rename above. Run the full suite;
      update any assertion that names a renamed field/action string on the way
      (`Contracts/PersistedShapeTests.fs`, `Contracts/ActionNamesTests.fs`, etc.).
- [ ] 10. Scratch projects `GNUCashAccess` (17 violations found), `MeasureScan` (7),
      `PdfProcessing` (0), `TestMsGraphToEmails` (0) — **deliberately deferred**. These are
      standalone experiments per CLAUDE-project.md, not part of the shipped app
      (`MyDogsbody.sln`'s app path) or its test suite. See outcome.md for the reasoning.
- [x] 11. `dotnet build MyDogsbody.sln` clean, `dotnet test` full suite green except one
      pre-existing, unrelated failure. See outcome.md for totals.
