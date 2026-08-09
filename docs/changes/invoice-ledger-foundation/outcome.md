# Outcome — Invoice ledger foundation

Change **#1 of 7**. See [`requirements.md`](requirements.md), [`design.md`](design.md),
[`tasks.md`](tasks.md).

## Gate

- `dotnet build MyDogsbody.sln` — **0 errors**, 2 pre-existing warnings (both in
  `MyDogsbody.Tests`, neither touched by this change: `FS0760` in `PdfDocumentReaderTests.fs`,
  `FS0020` in `CredentialDependencyContractTests.fs`).
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` — **386 tests, 0 failures, 0 skips**, all
  four levels present:

  | Level | Before | After | Added |
  | --- | --- | --- | --- |
  | Unit | 72 | 159 | +87 |
  | Integration | 45 | 74 | +29 |
  | Contract | 80 | 138 | +58 |
  | E2E | 7 | 15 | +8 |
  | **Total** | **204** | **386** | **+182** |

- `Contracts/DomainIsolationTests.fs` (3 tests) and the `AssertDomainReferencesNothing` build
  target both still pass — `MyDogsbody.Domain` gained `Suppliers/` and still has zero
  `ProjectReference` elements.
- `MyDogsbody/MainWindow.xaml.cs` and `MyDogsbody/MyDogsbody.csproj` are untouched (`git status`
  shows no changes to either).

## What friction #11 turned up

Friction #11 (background.md) predicted the main database's first real execution would surface
something beyond the two defects already fixed by inspection (the leaked `SqliteConnection`, the
missing `PRAGMA foreign_keys`). It found a third, plus a documentation inaccuracy:

1. **`NU1605` is a hard error on `dotnet run`, not just a warning on `dotnet build MyDogsbody.sln`.**
   Wiring `MyDogsbody.Startup` to `MyDogsbody.Database.Migrations` pulls in FluentMigrator, whose
   own dependency chain wants `Microsoft.Extensions.DependencyInjection.Abstractions` 9.0.3;
   `MyDogsbody.Startup.fsproj` pinned that package explicitly at 9.0.0. `dotnet build MyDogsbody.sln`
   reported this as a `NU1605` package-downgrade **warning** and still succeeded, so the whole-suite
   gate looked clean. `dotnet run --project MyDogsbody\MyDogsbody.csproj` — the WPF host — treated
   the identical condition as an **error** and refused to build at all. Fixed by bumping the pin to
   9.0.3 in `MyDogsbody.Startup.fsproj`. Recorded in CLAUDE-project.md's *Build state* section as a
   standing rule: keep that pin at or above whatever FluentMigrator resolves to, and don't trust
   `dotnet build MyDogsbody.sln` alone to catch a regression here — it has to be `dotnet run`.

2. **`MyDogsbody.db` (and `Credentials.db`) land at the repository root, not
   `bin\Debug\net9.0-windows\`, when the app is launched with the documented command from the
   repository root.** CLAUDE-project.md previously claimed `dotnet run` puts these files under
   `bin\Debug\net9.0\` — that was inaccurate for the command as actually documented and actually
   run: `dotnet run` does not change directory into the build output before launching the host
   process, so relative paths resolve against wherever the command was invoked from. Corrected in
   three places in CLAUDE-project.md (*Commands → Run*, *Testing in this codebase*, *Composition
   root*). Neither file is gitignored at the repository root; both were deleted after manual
   verification rather than left for `git status` to trip over.

3. **Dapper.FSharp's `excludeColumn`/`includeColumn` custom operations don't resolve inside an
   `insert { }` block with no `for x in table do`.** Not a runtime surprise — a compile-time one,
   caught immediately by the build, but worth recording because the error message
   (`Expression<Func<'0,'1>>` vs. a stray `unit -> 'a -> 'b`) does not point at the cause. Worked
   around with plain parameterised Dapper SQL for the two inserts, folding
   `SELECT last_insert_rowid()` into the same command as the `INSERT`. Documented in
   CLAUDE-project.md's *Storage → Main database* section so the next table pair doesn't rediscover
   it the same way.

4. **FluentMigrator's SQLite generator refuses `Create.ForeignKey`** ("Foreign keys are not
   supported in SQLite") — SQLite requires a foreign key declared inline in `CREATE TABLE`, which
   the fluent `Create.Table()` builder has no syntax for. `Migration_20260809000002` uses
   `Execute.Sql` for that one statement instead. Documented alongside the point above.

## Manual verification (9.4)

The app was run via `dotnet run --project MyDogsbody\MyDogsbody.csproj` from the repository root.
A supplier was added, edited, and deleted through `/settings/suppliers` in the running app. The
resulting `MyDogsbody.db` was inspected directly afterward and confirmed: all four migrations
applied (`VersionInfo` holds `20251104000001`, `20251104000002`, `20260809000001`,
`20260809000002`); one `Suppliers` row remained, matching the edited name and payment term; the
deleted supplier's row and its `SupplierMatchers` rows were both gone. `MyDogsbody.db` and
`Credentials.db` were deleted from the repository root afterward.

## Design deviations

- **`SupplierError` gained a seventh case, `PaymentTermInvalid of reason: string`, not in
  `design.md`'s six-case listing.** `AddSupplierWorkflow`/`EditSupplierWorkflow` validate a payment
  term, and the documented DU had no case for that failure — the alternative was reusing
  `SupplierNameInvalid` for a payment-term message, which would render a misleading sentence in the
  UI. Follows the same "expected failure, wraps `ApplicationException`, unlogged" rule as every
  other validation case in `SupplierApiMappers.toMyDogsbodyException`.
- **`PRAGMA foreign_keys = ON`** (design decision 6) is implemented via the `Foreign Keys=True`
  Microsoft.Data.Sqlite connection-string keyword rather than an explicit `PRAGMA` statement issued
  by `DatabaseContextSetup`. Same observable effect — the connection enforces foreign keys — without
  needing to track the connection's open/closed state to know when to issue it; it self-applies on
  every open regardless of who opens the connection.

## Not implemented (Optional, deferred)

- **O.1** "used by *n* templates" column — nothing consumes suppliers until change #2.
- **O.2** Sort/filter on the suppliers table — `MudTable` gives both cheaply if the table's ~30-row
  assumption stops holding; not needed to prove the database path.
- **O.3** Deduplicating identical match rules on save — a repeated rule changes no outcome.
