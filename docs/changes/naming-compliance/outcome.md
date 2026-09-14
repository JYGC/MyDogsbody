# Naming compliance — outcome

## Result

- `dotnet build MyDogsbody.sln`: **0 errors**, 2 warnings (both pre-existing, unrelated —
  `FS0760` in `PdfDocumentReaderTests.fs`, `FS0020` in `ScanWindowStoreTests.fs`; confirmed
  present in these files at the branch's starting commit).
- `dotnet test MyDogsbody.Tests/MyDogsbody.Tests.fsproj`: **1561/1562 passing.**
- The one failure — `SqliteConnectionPoolingTests.``every SQLite connection string a test
  builds disables pooling``` — is **pre-existing and unrelated to this change**. Confirmed by
  diffing the flagged lines (`Contracts/InvoiceDependencyContractTests.fs`,
  `Contracts/InvoicePersistedShapeTests.fs`, `Database/InvoiceStoreTests.fs`,
  `Database/ScanWindowStoreTests.fs`, `E2E/InvoicesTestHarness.fs`,
  `Startup/InvoiceApiFactoryTests.fs`, `Startup/ScanWindowApiFactoryTests.fs`) against this
  branch's starting commit (`git show HEAD:<file>`) — byte-identical, never touched by this
  change. Per CLAUDE.md, naming this here rather than claiming the suite fully green; fixing
  it is a change of its own (add `;Pooling=False` to each connection string).
- 177 files changed, spanning every project from `MyDogsbody.Domain` through
  `MyDogsbody.Tests`, plus the WPF host's `MainWindow.xaml.cs`.

## What actually needed changing

The codebase's **public surface** (workflow function names, record types and their fields,
DU cases, module names, dependency function types, UI-facing API records) turned out to
already be almost entirely compliant — CLAUDE.md's naming discipline was evidently already
the working convention for anything crossing a file or project boundary. Zero public
symbols were renamed in production code (Domain, Builders/Exceptions, Database,
Integrations, Logging, Startup, UI.Types, UI.Portal) as a result of this change; the two
already-compliant statuses that mattered most for scope were:

- **Persisted shapes were already compliant.** Every SQLite record field in
  `MyDogsbody.Database.Models/Models.fs` and every LiteDB entity property across
  `MyDogsbody.Integrations.Google.Database.Models` and
  `MyDogsbody.Integrations.Thunderbird.Database.Models` was already a full descriptive word
  (or the exempted `Id`). **No SQLite migration and no `[BsonId]` fix-up was needed** —
  despite the decision going in to rename persisted names with migrations if they needed it.
- **`MyDogsbody.UI.Types`** (17 files) needed **zero changes** — already fully compliant.

What actually violated the rule was almost entirely **local, function-scoped identifiers**:
lambda parameters (`fun a -> a.Id`), catch/match bindings (`with ex ->`, `Error err ->`),
fold/loop accumulators (`acc`, `sb`), and a handful of short parameter names
(`conn`, `deps`, `dir`, `cmd`). None of these cross a file boundary, so none required
updating a second call site. The one true rename with any reach was
`ScanAcc` → `ScanAccumulator` (a private type in `ScanForInvoicesWorkflow.fs`), which also
needed a stale comment fixed in `ScanForInvoicesWorkflowTests.fs`.

## Scope decisions made along the way (see design.md for the originals)

- Exempted "already a full word" initialisms: `Id`, `Api`, `Sql`, `Url`, `Uri`, `Html`,
  `Json`, `Http`/`Https`, `Pdf`, `Ui`, `Xml`, `Guid`, `Csv`. Applied consistently; no
  disagreements surfaced during execution.
- Generic type parameters (`'T`, `'a`) kept normal F# convention — out of scope. Confirmed
  this codebase declares very few explicit ones, so the decision had little practical effect.
- `_` (discard) exempt throughout.

## Process note: subagent rate limit

Phases 1–8 (all of production code) and the first `MyDogsbody.Tests` sub-phase (`Fixtures`)
ran as sequential subagents, each verified to build clean before the next started. For the
remaining `MyDogsbody.Tests` subfolders (Domain, Database, Integrations, UI, Contracts, E2E,
Startup+Logging), 7 subagents were launched in parallel, each scoped to a non-overlapping
folder. One (`UI`) completed; the other six hit an account-wide rate limit mid-edit and
stopped partway through their assigned files. The working tree remained buildable throughout
(each subagent's edits were atomic per-identifier changes, never left mid-syntax), so the
remaining violations in those six folders were located via repo-wide grep and finished
directly rather than by re-launching subagents. One genuine bug was introduced and caught
during this cleanup: a `sed` pass mid-flight had dropped a backslash inside a verbatim string
literal in `ThunderbirdStoreTests.fs` (`@"C:profile|account2"` instead of
`@"C:\profile|account2"`), which the test suite caught as a real assertion failure and was
fixed before the final green run.

## Deliberately out of scope

`GNUCashAccess` (17 violations found), `MeasureScan` (7) — standalone scratch/experiment
projects per CLAUDE-project.md's own description ("Standalone experiments"), not part of
`MyDogsbody.sln`'s shipped app path or the test suite. Deferred rather than fixed, consistent
with CLAUDE.md's "skip specs for exploratory/prototype work" carve-out applied to the same
spirit here. `PdfProcessing` and `TestMsGraphToEmails` (both scratch too) had zero violations
already.

## Testing note

This is a pure rename with no behavior change, so CLAUDE.md's "failing unit test lands
first" rule doesn't apply in the usual sense — there is no new behavior to specify a test
for. Compliance was verified by: the full existing suite passing unchanged (proving no
behavior moved), and a full-repo grep sweep for the violation patterns (single-letter
lambda/`let`/`for` bindings, and the glossary abbreviations) coming back empty across every
project in scope.
