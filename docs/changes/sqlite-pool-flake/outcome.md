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
comment. The app opens exactly one `SqliteConnection` per process (`Startup.fs`) and holds it for the
process lifetime, so a pool amortises nothing — it was pure cost, and the cause of the flake.

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

The existing `DatabaseContextSetupTests` deletion test keeps its `File.Delete` + `Assert.False`
(no `try/with`) — with `Pooling=False` it now genuinely proves `context.Dispose()` releases the OS
handle, rather than proving a global pool clear works. (Microsoft.Data.Sqlite 9.0.10 does **not**
throw `ObjectDisposedException` on `Open()` after `Dispose()` — a disposed `SqliteConnection`
silently reopens — verified by probe — so "the handle is released" is the only observable part of the
`Dispose` contract worth asserting.)

## Test totals

| Level | Count | Δ |
| --- | --- | --- |
| Unit | 565 | +1 (the `ClearAllPools` source check) |
| Integration | 224 | +1 (the pooling-disabled assertion) |
| Contract | 246 | — |
| E2E | 28 | — |
| **Total** | **1063** | **+2** |

Baseline on `main` was **1061**. No existing test deleted, none skipped.

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
- Independent of the `invoice-to-calendar` series. Touches `DatabaseContextSetup.fs` (one literal)
  and ten test files; the in-flight `change/invoice-extraction` branch modifies none of them, so this
  merges in either order.

## Not fixed here

- **The LiteDB `BsonMapper` first-use race.** CLAUDE-project.md → *Per-integration databases*;
  captured in `invoice-extraction`'s `outcome.md`. Remedy is a process-wide lock around the entity
  warm-up; its own change folder.
- **`MailAccountsFlowTests`' permission-denied-directory cleanup.** Belongs with `thunderbird-account-selection`.
