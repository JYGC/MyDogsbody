# Outcome — the SQLite test harnesses clear the process-global connection pool

Branch `change/sqlite-pool-flake`, cut from `main` at `9b0d4ce`.

## Status

**Done. Build clean. Suite green, zero skips.** The ten harnesses no longer call the process-global
`SqliteConnection.ClearAllPools()`; `MyDogsbody.Database/DatabaseContextSetup.fs` opens with
`Pooling=False`, and every SQLite connection string the tests build carries `;Pooling=False`, so a
`Dispose()` / `use` releases the file handle with nothing to clear.

## What changed

### Production — one connection-string literal

`DatabaseContextSetup.createDatabaseContext`:
`Data Source={path};Foreign Keys=True` → `Data Source={path};Foreign Keys=True;Pooling=False`, with a
comment stating the trade.

**The trade, measured (corrected in review round 1).** This section first said a pool "amortises
nothing" because the app holds one connection. That was wrong: `Startup.fs` holds one
`SqliteConnection` *object*, but the underlying handle is opened and closed **per store operation** —
`SupplierStore`/`TemplateStore`'s `inTransaction` around every write, and Dapper around every
`SelectAsync` on a closed connection. Microsoft.Data.Sqlite 9.0.10, one connection object, 2000
open/query/close cycles:

| | per cycle |
| --- | --- |
| `Pooling=True` (default) | 0.090 ms |
| `Pooling=False` | 0.470 ms |
| cost of disabling | **+0.38 ms per open, 5.2×** |

A suppliers page load is two cycles (+0.8 ms), a write one (+0.4 ms) — invisible in a desktop UI, and
knowingly traded for a suite that no longer fails ~2 runs in 45.

### Tests — ten files, fourteen `ClearAllPools()` sites removed

| File | `ClearAllPools()` sites | Also |
| --- | --- | --- |
| `Contracts/SupplierApiContractTests.fs` | 1 | dropped unused `open Microsoft.Data.Sqlite` |
| `Contracts/SupplierDependencyContractTests.fs` | 1 | dropped unused `open` |
| `Contracts/SupplierPersistedShapeTests.fs` | 1 | keeps `open` (raw connection in `columnNames`) |
| `Database/DatabaseContextSetupTests.fs` | 2 | `migrationConnectionString` helper; deletion test now proves `Dispose()` releases the handle |
| `Database/MigrationTestHelpers.fs` | 1 | `;Pooling=False` on its internal `…;Foreign Keys=True` string |
| `Database/SupplierStoreTests.fs` | 1 | dropped `Assert.False(File.Exists …)` |
| `Database/TemplateStoreTests.fs` | 1 | dropped `Assert.False(File.Exists …)` |
| `E2E/SuppliersTestHarness.fs` | 1 | dropped unused `open` |
| `Startup/SupplierApiFactoryTests.fs` | 2 | dropped unused `open` |
| `Startup/TemplateApiFactoryTests.fs` | 3 | dropped unused `open` |

Every `let connectionString = $"Data Source={databaseFilePath}"` gained `;Pooling=False`; every hard
`File.Delete databaseFilePath` became `try File.Delete databaseFilePath with _ -> ()`.

### Tests added (regression prevention)

- `Database/DatabaseContextSetupTests.fs` — **`createDatabaseContext opens with pooling disabled and
  foreign keys on`**: opens a temp context and, via
  `SqliteConnectionStringBuilder(context.GetDatabaseConnection().ConnectionString)`, asserts
  `.Pooling` is `false` and `.ForeignKeys` is `Nullable true`. Red before the production line, green
  after.
- `Database/SqliteConnectionPoolingTests.fs` (new) — **`no test source file calls
  SqliteConnection.ClearAllPools`**: walks every `.fs` under `MyDogsbody.Tests` (excluding `bin`/`obj`
  and the checker itself) and fails if any contains the literal call. Red before the ten edits (it
  named all ten), green after. Same shape as `SuppliersBrowserModuleCreatorsTests`'s "no `Async.Start`"
  check.
- `Database/SqliteConnectionPoolingTests.fs` — **`every SQLite connection string a test builds
  disables pooling`** (added in review round 1): the same walk, failing on any line that builds a
  `Data Source=` string without `;Pooling=False`. This is the half of the rule that has to hold going
  forward — `;Pooling=False` is what makes the temp file deletable, whereas dropping `ClearAllPools()`
  only stops harnesses trampling each other. Red proof: stripping `;Pooling=False` from
  `Database/SupplierStoreTests.fs:41` failed with *"these SQLite connection strings do not disable
  pooling … Database\SupplierStoreTests.fs:41"*; restoring it went green.

The existing `DatabaseContextSetupTests` deletion test keeps its `File.Delete` + `Assert.False`
(no `try/with`) — with `Pooling=False` it now genuinely proves `context.Dispose()` releases the OS
handle, rather than proving a global pool clear works. (Microsoft.Data.Sqlite 9.0.10 does **not**
throw `ObjectDisposedException` on `Open()` after `Dispose()` — a disposed `SqliteConnection`
silently reopens — verified by probe — so "the handle is released" is the only observable part of the
`Dispose` contract worth asserting.)

## Test totals

| Level | Count | Δ |
| --- | --- | --- |
| Unit | 566 | +2 (the `ClearAllPools` source check; the `Pooling=False` source check) |
| Integration | 224 | +1 (the pooling-disabled assertion) |
| Contract | 246 | — |
| E2E | 28 | — |
| **Total** | **1064** | **+3** |

Baseline at the commit this branch was cut from (`main` at `9b0d4ce`) was **1061**. No existing test
deleted, none skipped.

CLAUDE-project.md → *Build state* is deliberately **not** updated here: `main` has since moved to
`3834aa1` and records `1270 tests — 706 Unit, 270 Integration, 264 Contract, 30 E2E` for
`credentials-per-provider`'s head. Writing this branch's `1064` over it would be wrong the moment the
merge lands; the merge should set **`1273 tests — 708 Unit, 271 Integration, 264 Contract, 30 E2E`**
(`main`'s 706 / 270 / 264 / 30 plus this change's +2 Unit and +1 Integration) after re-measuring.
*(Round 1 wrote `709 / 271 / 264 / 30` here, which sums to 1274 and contradicts its own total; round 2
corrected the Unit figure to 708. Re-measure at merge rather than trusting either — the eleven
`;Pooling=False` additions the merge needs add no tests, but #18's own head may have moved again.)*
See the base-has-moved note in `tasks.md`.

## The flake, characterised

Review round 5 of `invoice-extraction` measured this cause at **~2 failures in 45 full-suite runs**
(`ObjectDisposedException: 'SQLitePCL.sqlite3'` out of `SqliteCommand.ExecuteReader`), pre-existing at
the reviewed head.

**25 full-suite runs on this branch: zero occurrences of that exception.** 22 runs green; the 3
failures were pre-existing flakes this change does not touch and does not claim to fix:

| Failures in 25 runs | Cause | Owner |
| --- | --- | --- |
| 2 | `InvalidOperationException: Collection was modified` → `LiteDB.BsonMapper.SerializeObject` → `ThunderbirdDatabaseContextModule.fs:16` | the documented LiteDB `BsonMapper` first-use race — its own change folder |
| 1 | `UnauthorizedAccessException` deleting a deliberately permission-denied temp dir in `MailAccountsFlowTests` cleanup | a `thunderbird-account-selection` test-hygiene issue, pre-existing on `main` |

The mechanism is removed, not narrowed: with no `ClearAllPools()` call anywhere and pooling disabled,
one collection's teardown cannot dispose another's connection.

## Notes

- The ten harnesses now match — and improve on — the pattern `invoice-extraction`'s newer SQLite
  harnesses use (`try File.Delete … with _ -> ()`, no `ClearAllPools()`): those *leak* the temp file
  when a pooled handle lingers; with `Pooling=False` there is no lingering handle, so the file
  actually deletes.
- Independent of the `invoice-to-calendar` series in the files it edits: `DatabaseContextSetup.fs`
  (one literal) and the ten test files that were calling `ClearAllPools()` at `9b0d4ce`. **But the
  base has moved** — `main` is now `3834aa1`, with `invoice-extraction` (#18) and
  `credentials-per-provider` (#20) merged. `git merge-tree origin/main HEAD` is conflict-free, yet the
  merge is not complete on its own. Measured against the merged tree itself
  (`git merge-tree --write-tree origin/main HEAD` → `04f4f07`, then grepped for lines building a
  `Data Source=` string with no `;Pooling=False`): **eleven such lines across seven files**, all from
  #18 — `Contracts/InvoiceDependencyContractTests.fs` (49 **and** 51: the `setupMigrations` string and
  the raw `new SqliteConnection` beside it), `Contracts/InvoicePersistedShapeTests.fs` (15),
  `Database/InvoiceStoreTests.fs` (27), `Database/ScanWindowStoreTests.fs` (22),
  `E2E/InvoicesTestHarness.fs` (45), `Startup/InvoiceApiFactoryTests.fs` (25, 50, 76, 107) and
  `Startup/ScanWindowApiFactoryTests.fs` (18). *(Review round 1 said "eight" while listing ten sites;
  round 2 measured eleven off the merge tree and corrected both numbers here and in `tasks.md`.)*
  The `ClearAllPools` half of the check is green in that same merged tree — every site still calling
  it on `main` lives in one of the ten files this branch rewrites — so only the `;Pooling=False` half
  goes red. `main`'s CLAUDE-project.md *Build state* also still says the SQLite harnesses "leak their
  GUID-named temp file instead if the pool still holds a handle" — a sentence this change makes
  obsolete. The new `every SQLite connection string a test builds disables pooling` check turns that
  gap into a red test at merge time instead of a silent one.

## Not fixed here

- **The LiteDB `BsonMapper` first-use race.** CLAUDE-project.md → *Per-integration databases*;
  captured in `invoice-extraction`'s `outcome.md`. Remedy is a process-wide lock around the entity
  warm-up; its own change folder.
- **`MailAccountsFlowTests`' permission-denied-directory cleanup.** Belongs with `thunderbird-account-selection`.
- **`Startup.fs`'s migration connection string.** `MigrationSetup.setupMigrations $"Data Source={mainDatabasePath}"`
  still pools, leaving one FluentMigrator handle on `MyDogsbody.db` in a pool for the process
  lifetime. Nothing observable follows (the app holds the file open anyway), but it is the last
  pooled SQLite connection in the product; adding `;Pooling=False` there is a second production line
  outside this change's agreed scope.
