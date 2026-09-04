# Tasks — the SQLite test harnesses clear the process-global connection pool

Branch `change/sqlite-pool-flake`, cut from `main` at `9b0d4ce`. It touches only
`MyDogsbody.Database/DatabaseContextSetup.fs` (one connection-string literal) and the ten test files
that were calling `ClearAllPools()` at that commit.

> **Base has moved (review round 1).** `main` is now `3834aa1` — `invoice-extraction` (#18, `6f449ab`)
> and `credentials-per-provider` (#20, `3834aa1`) both merged after this branch was cut. The merge is
> still textually clean (`git merge-tree` produces no conflict), but "merges in either order" is not
> the whole story any more: #18 added further SQLite connection strings that carry no `;Pooling=False`.
> **Measured on the merge itself, not inferred** — `git merge-tree --write-tree origin/main HEAD`
> (tree `04f4f07`), then grepping that tree for lines building a `Data Source=` string without the
> keyword — there are **eleven such lines across seven files** (review round 1 said "eight", which
> undercounts; round 2 corrected it):
>
> | File (in the merged tree) | Lines |
> | --- | --- |
> | `Contracts/InvoiceDependencyContractTests.fs` | 49, 51 — **two**, the `setupMigrations` string *and* the raw `new SqliteConnection` beside it |
> | `Contracts/InvoicePersistedShapeTests.fs` | 15 |
> | `Database/InvoiceStoreTests.fs` | 27 |
> | `Database/ScanWindowStoreTests.fs` | 22 |
> | `E2E/InvoicesTestHarness.fs` | 45 |
> | `Startup/InvoiceApiFactoryTests.fs` | 25, 50, 76, 107 — **four** |
> | `Startup/ScanWindowApiFactoryTests.fs` | 18 |
>
> The `ClearAllPools` half of the check is *green* in that same merged tree: every site that still
> called it on `main` is in one of the ten files this branch rewrites, so the merge removes them all.
> Only the `;Pooling=False` half goes red. CLAUDE-project.md's *Build state* on `main` also still says
> the SQLite harnesses "leak their GUID-named temp file instead if the pool still holds a handle".
> Task 5.2's check turns those eleven lines into a red test at merge time rather than a silent gap —
> completing the merge means adding `;Pooling=False` to all eleven and correcting that *Build state*
> sentence and its totals.

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
      `$"Data Source={databaseFilePath};Foreign Keys=True;Pooling=False"`, with a comment saying why:
      a pooled handle survives `Dispose()` and keeps the file locked, which is what made temp-database
      cleanup fail. **Not** "pooling is cost-only" — see 5.1, which measured that claim false; the
      comment states the trade (+0.38 ms per store operation) instead of denying it.
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

## Phase 5 — PR review round 1

- [x] **5.1** Correct the rationale. Measured on Microsoft.Data.Sqlite 9.0.10 (2000 open/query/close
      cycles, one connection object): pooled **0.090 ms**/cycle, unpooled **0.470 ms**/cycle —
      **+0.38 ms, 5.2×**. The app holds one `SqliteConnection` *object*, not one open handle:
      `inTransaction` opens/closes per write and Dapper opens/closes around every `SelectAsync`, so
      "a pool amortises nothing here" was false. Reworded in `DatabaseContextSetup.fs`,
      CLAUDE-project.md → *Storage → Main database*, `design.md` (*Why C*) and `bugfix.md`
      (*Unchanged Behavior*). The decision is unchanged — sub-millisecond per page load against a
      flake at ~2 runs in 45 — only the reasoning is now true.
- [x] **5.2** Enforce the other half of the rule. CLAUDE-project.md now mandates `;Pooling=False` on
      every SQLite connection string a test builds, but only the `ClearAllPools` half was guarded.
      `Database/SqliteConnectionPoolingTests` gains **`every SQLite connection string a test builds
      disables pooling`**, over the same source walk. Red proof: `;Pooling=False` stripped from
      `Database/SupplierStoreTests.fs:41` → *"these SQLite connection strings do not disable pooling
      … Database\SupplierStoreTests.fs:41"*; restored → green. The walk also now skips `bin`/`obj` by
      whole path segment (`Contains "bin\"` missed a file sitting directly in a folder named `bin`).

## Not in scope

- **The LiteDB `BsonMapper` first-use race.** A separate known flake (CLAUDE-project.md →
  *Per-integration databases*; captured in `invoice-extraction`'s `outcome.md`). Its remedy is a
  process-wide lock around the entity warm-up and wants its own change folder.
- **Changing how the application opens its main connection.** `Startup.fs` keeps its single
  process-lifetime connection object; `Pooling=False` costs it +0.38 ms per store operation (5.1) and
  nothing observable.
- **`Startup.fs`'s migration connection string.** `MigrationSetup.setupMigrations $"Data Source={mainDatabasePath}"`
  still pools, so one FluentMigrator handle on `MyDogsbody.db` goes into a pool at startup and stays
  there. Harmless — the app holds the file open regardless — but it is the last pooled SQLite
  connection in the product, and adding `;Pooling=False` is a second production line this change was
  scoped not to take. Raise it as its own one-liner if the handle ever matters.
