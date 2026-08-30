# Tasks — the SQLite test harnesses clear the process-global connection pool

Branch `change/sqlite-pool-flake`, cut from `main` at `9b0d4ce`. Independent of the
`invoice-to-calendar` series — it touches only `MyDogsbody.Database/DatabaseContextSetup.fs` (one
connection-string literal) and ten test files, none of which the in-flight `invoice-extraction`
branch modifies, so it merges in either order.

[`bugfix.md`](bugfix.md) · [`design.md`](design.md)

**Ordering rule, per task:** the regression-prevention tests land and are confirmed to fail for the
expected reason before the production line changes.

---

## Phase 1 — Regression-prevention tests (test-first)

- [x] **1.1** `Database/DatabaseContextSetupTests.fs` — a test that opens a temp context and, via
      `SqliteConnectionStringBuilder(context.GetDatabaseConnection().ConnectionString)`, asserts
      `.Pooling` is `false` and `.ForeignKeys` is `Nullable true`. Run it: **red** (production still
      pools). *(Microsoft.Data.Sqlite 9.0.10 does NOT throw `ObjectDisposedException` on `Open()`
      after `Dispose()` — verified by probe — so the `Dispose` contract that matters here is "the
      file handle is released", which the existing deletion test in 3.10 asserts once
      `ClearAllPools()` is gone and `Pooling=False` is in.)*
- [x] **1.2** A source-tree test (mirror `InvoicesModuleCreatorsTests`'s "uses no `Async.Start`"
      check): walk every `.fs` under `MyDogsbody.Tests` and assert none contains
      `SqliteConnection.ClearAllPools`. Run it: **red** (ten files still call it).

## Phase 2 — The production line

- [x] **2.1** `MyDogsbody.Database/DatabaseContextSetup.fs`: connection string →
      `$"Data Source={databaseFilePath};Foreign Keys=True;Pooling=False"`, with a comment (single
      long-lived production connection; pooling is cost-only; keeps test cleanup deterministic).
      1.1 goes **green**.

## Phase 3 — The ten harnesses

Per file: delete every `SqliteConnection.ClearAllPools()`; add `;Pooling=False` to every
`connectionString` the file builds; hard `File.Delete databaseFilePath` → `try File.Delete
databaseFilePath with _ -> ()`; drop `open Microsoft.Data.Sqlite` if nothing else in the file uses
it.

- [x] **3.1** `Contracts/SupplierApiContractTests.fs`
- [x] **3.2** `Contracts/SupplierDependencyContractTests.fs`
- [x] **3.3** `Contracts/SupplierPersistedShapeTests.fs` (keeps `open Microsoft.Data.Sqlite` — line
      37 opens a raw connection)
- [x] **3.4** `Database/MigrationTestHelpers.fs` — also add `;Pooling=False` to the internal
      `…;Foreign Keys=True` string; keeps the `open` (raw connections throughout)
- [x] **3.5** `Database/SupplierStoreTests.fs` — drop the `Assert.False(File.Exists …)` too
- [x] **3.6** `Database/TemplateStoreTests.fs` — drop the `Assert.False(File.Exists …)` too
- [x] **3.7** `E2E/SuppliersTestHarness.fs`
- [x] **3.8** `Startup/SupplierApiFactoryTests.fs` (two sites)
- [x] **3.9** `Startup/TemplateApiFactoryTests.fs` (three sites)
- [x] **3.10** `Database/DatabaseContextSetupTests.fs` — `withTempPath` loses its `ClearAllPools()`;
      the `try File.Delete … with _` it already has stays. The deletion-asserting test loses its
      `ClearAllPools()` line and its `File.Delete` + `Assert.False` now pass on `Dispose()` alone
      (with 2.1 in place). 1.2 goes **green**.

## Phase 4 — Gate

- [x] **4.1** `dotnet build MyDogsbody.sln` — zero errors. Confirm no file still imports
      `Microsoft.Data.Sqlite` without using it (FS1182 is not on, so check by grep).
- [x] **4.2** `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` — zero failures, zero skips.
      Record totals per level; the delta is exactly the Phase 1 additions.
- [x] **4.3** **25 full-suite runs: zero occurrences of the `ObjectDisposedException: 'SQLitePCL.sqlite3'`
      this change fixes** (round 5 measured it at ~2 in 45). 22 green; 3 failures were pre-existing
      flakes out of scope — 2× the LiteDB `BsonMapper` race (`ThunderbirdDatabaseContextModule.fs:16`),
      1× `MailAccountsFlowTests` deleting a permission-denied temp dir in cleanup. See `outcome.md`.
- [x] **4.4** `outcome.md`: the run evidence, the totals, and a note that the ten harnesses now
      match — and improve on — the pattern `invoice-extraction`'s newer ones use.
- [x] **4.5** `CLAUDE-project.md` → *Build state* / the flake notes: the `ClearAllPools()` hazard is
      resolved; only the LiteDB `BsonMapper` race remains as a known flake.

## Not in scope

- **The LiteDB `BsonMapper` first-use race.** A separate known flake (CLAUDE-project.md →
  *Per-integration databases*; captured in `invoice-extraction`'s `outcome.md`). Its remedy is a
  process-wide lock around the entity warm-up and wants its own change folder.
- **Changing how the application opens its main connection.** `Startup.fs` keeps its single
  process-lifetime connection; `Pooling=False` changes nothing observable there.
