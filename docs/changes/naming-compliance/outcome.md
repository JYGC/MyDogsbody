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

## Addendum: comment-word-limit raised from 80 to 200

CLAUDE.md's naming section originally read "comments only when the name would exceed 80
words"; this was raised to 200. Raising the limit makes the rule *stricter*, not looser — a
comment compensating for a name that would need 81-200 words was allowed under the old
threshold and is not allowed under the new one, so this could in principle have pulled
additional comments into "must become a name."

Audited the whole repo for this before concluding no code needed to change. Extracted every
comment block of 25+ words (several hundred), read a sample spanning the full length range
(25 to 558 words), and specifically filtered for blocks *without* the rationale language this
codebase otherwise uses throughout (`because`, `since`, `used to`, `Q#.#`, `requirements.md`,
`design.md`, `PR #`, `so a`/`so the`/`so it`, etc.) to surface anything that might be a pure
"what is this" description rather than "why" — 55 blocks at 60+ words and 131 more at 25-59
words matched that filter. Read every one.

**Every block, at every length, is "why" content** — design rationale tied to
`requirements.md`/`design.md` decisions, historical bug reports with measured evidence, race
conditions and invariants, or workaround explanations — never a description of *what* an
already-named thing is that merely happens to be long. The general coding-style rule
("comments only for a hidden constraint, a subtle invariant, a workaround for a specific bug,
behavior that would surprise a reader") already exempts this content regardless of word
count; the naming section's threshold was never the operative constraint for any comment in
this codebase, at 80 words or at 200. No identifier was renamed and no comment was removed as
a result of this change — only the CLAUDE.md limit itself.

## Testing note

This is a pure rename with no behavior change, so CLAUDE.md's "failing unit test lands
first" rule doesn't apply in the usual sense — there is no new behavior to specify a test
for. Compliance was verified by: the full existing suite passing unchanged (proving no
behavior moved), and a full-repo grep sweep for the violation patterns (single-letter
lambda/`let`/`for` bindings, and the glossary abbreviations) coming back empty across every
project in scope.
