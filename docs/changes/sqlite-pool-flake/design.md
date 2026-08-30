# Design — the SQLite test harnesses clear the process-global connection pool

## Root cause analysis

**Microsoft.Data.Sqlite pools connections by default.** `SqliteConnection.Dispose()` returns the
connection to a pool keyed by its (normalised) connection string rather than closing the underlying
`sqlite3` handle. On Windows that handle keeps the database file locked, so a test that disposes its
`DatabaseContext` and then calls `File.Delete` on the temp file gets an `IOException` — the file is
still open.

**The workaround the harnesses reached for was `SqliteConnection.ClearAllPools()`.** It disposes
every idle pooled connection in the process and marks checked-out ones for disposal on return. Called
in a harness's `finally`, it releases that harness's handle so the delete succeeds.

**But it is process-global.** xUnit runs test collections in parallel by default. When harness A's
`finally` runs `ClearAllPools()` while harness B is between `connection.Open()` and
`command.ExecuteReader()` on a pooled connection to a *different* file, B's connection is disposed
underneath it and B throws `ObjectDisposedException: 'SQLitePCL.sqlite3'`. The failure lands on
whichever test B happened to be running — so it looks like a defect in `TemplateApiFactoryTests` or
`SupplierApiFactoryTests` when the cause is `SupplierStoreTests`'s cleanup two collections away.

Confirmed in PR #18 review round 5: **45 full-suite runs at the untouched head `c1beff0` produced 5
failures**, this cause and the LiteDB `BsonMapper` race between them; **20 Integration-level-only runs
produced none**, which fits — the collision needs the parallel SQLite-touching collections
(Contract, E2E, Startup) running together.

Ten files call it, at fourteen sites:

| File | Sites | Shape |
| --- | --- | --- |
| `Contracts/SupplierApiContractTests.fs` | 1 | `ClearAllPools()` + hard `File.Delete` |
| `Contracts/SupplierDependencyContractTests.fs` | 1 | `ClearAllPools()` + hard `File.Delete` |
| `Contracts/SupplierPersistedShapeTests.fs` | 1 | `ClearAllPools()` + hard `File.Delete` |
| `Database/DatabaseContextSetupTests.fs` | 2 | one in `withTempPath`; one in a test that asserts the delete succeeds |
| `Database/MigrationTestHelpers.fs` | 1 | `ClearAllPools()` + `try File.Delete … with _` |
| `Database/SupplierStoreTests.fs` | 1 | `ClearAllPools()` + hard `File.Delete` + `Assert.False(File.Exists …)` |
| `Database/TemplateStoreTests.fs` | 1 | same as above |
| `E2E/SuppliersTestHarness.fs` | 1 | `ClearAllPools()` + hard `File.Delete` |
| `Startup/SupplierApiFactoryTests.fs` | 2 | `ClearAllPools()` + hard `File.Delete` |
| `Startup/TemplateApiFactoryTests.fs` | 3 | `ClearAllPools()` + hard `File.Delete` |

The `invoice-extraction` change's own newer SQLite harnesses (`InvoiceStoreTests`,
`ScanWindowStoreTests`, the invoice contract/E2E harnesses) already sidestep this — they do **not**
call `ClearAllPools()` and `try File.Delete … with _ -> ()`, leaking the temp file if the pool still
holds it. CLAUDE-project.md records that as the sanctioned pattern. This change brings the ten older
harnesses to a *better* place than that.

## The options

| Option | What | Verdict |
| --- | --- | --- |
| **A — leak the file** | Drop `ClearAllPools()`, `try File.Delete … with _ -> ()`, accept a stray temp file when the pool holds a handle | What the newer harnesses do. Works, but leaves GUID-named files in `%TEMP%` on every Windows run |
| **B — targeted `ClearPool`** | Replace `ClearAllPools()` with `SqliteConnection.ClearPool(conn)` — clears only that connection string's pool (Microsoft.Data.Sqlite ≥ 6.0; verified working on the pinned 9.0.10) | Fixes the cross-test interference and still deletes the file. But each test DB has **two** pools — the plain string the migrations use and the `;Foreign Keys=True` string `createDatabaseContext` builds — so a thorough clear needs both, per site |
| **C — disable pooling** ✅ | `createDatabaseContext` opens with `;Pooling=False`; test connection strings carry `;Pooling=False`. `Dispose()` / `use` then closes the handle immediately — no pool, nothing to clear, file deletes | Chosen |

**Why C.** Pooling exists to amortise connection setup across many opens of the same database. This
application opens **one** `SqliteConnection` per process in `Startup.fs` and holds it for the process
lifetime (CLAUDE-project.md → *Storage → Main database*); the migration run is one more, disposed.
There is no third. So pooling is pure cost here — it buys nothing in production and causes the flake
in tests. Disabling it:

- makes every `context.Dispose()` and every `use connection = new SqliteConnection(…)` release the OS
  handle synchronously, so `File.Delete` is reliable and no pool-clearing call is needed anywhere;
- removes the process-global side effect entirely rather than narrowing it;
- stops the suite leaking temp files, which option A does not.

Measured (`scratchpad` probe, deleted): a pooled `use` connection leaves the file locked
(`IOException`); the same with `;Pooling=False` deletes cleanly with no clear call.

## Changes

### Production — one line

`MyDogsbody.Database/DatabaseContextSetup.fs`:

```fsharp
new SqliteConnection($"Data Source={databaseFilePath};Foreign Keys=True;Pooling=False")
```

with a comment saying why (single long-lived connection; pooling is cost-only; keeps test cleanup
deterministic). No other production file changes — `Startup.fs` passes a path to
`createDatabaseContext`, and `MigrationSetup` receives whatever connection string its caller built.

### Tests — ten files

1. **Every `let connectionString = $"Data Source={databaseFilePath}"`** gains `;Pooling=False`, so the
   FluentMigrator runner (`MigrationSetup.setupMigrations connectionString`) and any raw
   `new SqliteConnection(connectionString)` the file opens do not pool either. Ditto
   `MigrationTestHelpers`' internal `…;Foreign Keys=True` string.
2. **Every `SqliteConnection.ClearAllPools()` is deleted** — 14 sites.
3. **Hard `File.Delete databaseFilePath`** becomes `try File.Delete databaseFilePath with _ -> ()`
   for defence in depth (an antivirus scanner, a slow handle release) — the newer harnesses'
   pattern. The `Assert.False(File.Exists databaseFilePath)` cleanup assertions in `SupplierStoreTests`
   and `TemplateStoreTests` are dropped: asserting the OS released a handle is not those tests'
   subject, and `Pooling=False` already makes it so.
4. **`DatabaseContextSetupTests`'s "a context … can be disposed and the file then deletes
   successfully"** keeps its deletion assertion — with `Pooling=False` it now genuinely proves
   `context.Dispose()` releases the handle, which is `createDatabaseContext`'s actual contract, rather
   than proving that a global pool clear works. Its `ClearAllPools()` line goes; the `File.Delete` +
   `Assert.False` stay and now pass on `Dispose()` alone.
5. **`open Microsoft.Data.Sqlite`** is removed from any file left with no other use of it (several
   only imported it for `ClearAllPools`).

## Error-handling approach

No error types change. This is test infrastructure and one production connection-string literal;
nothing in either ring gains or loses a `Result` case, an `ActionName`, or a mapper.

## Testing strategy

The four-level model does not map cleanly onto a test-infrastructure fix — there is no new workflow,
adapter, mapper or component. What this change owes instead:

### Characterization (before)

`PRAGMA foreign_keys reads back as 1` in `DatabaseContextSetupTests` already pins the FK behaviour
that must survive adding `;Pooling=False`. It is run unchanged and stays green — the connection-string
keyword `Foreign Keys=True` is independent of `Pooling`.

### Regression prevention (added)

- **`createDatabaseContext` disables pooling** — an integration test that opens a temp context and,
  through `SqliteConnectionStringBuilder(context.GetDatabaseConnection().ConnectionString)`, asserts
  `.Pooling` is `false` and `.ForeignKeys` is `Nullable true`. Fails if the production literal is
  reverted.
- **No harness calls `ClearAllPools()`** — a test that reads the `.fs` files under `MyDogsbody.Tests`
  from the source tree (the same shape as `InvoicesModuleCreatorsTests`'s "uses no `Async.Start`"
  check) and asserts none contains `SqliteConnection.ClearAllPools`. Fails if a new harness copies
  the old pattern.
- **`Dispose()` releases the file handle** — the existing `DatabaseContextSetupTests` deletion test,
  with its `ClearAllPools()` line removed, now proves exactly this: after `context.Dispose()` the
  temp file deletes. (Microsoft.Data.Sqlite 9.0.10 does not throw `ObjectDisposedException` on
  `Open()` after `Dispose()` — a disposed `SqliteConnection` silently reopens — so "the handle is
  released" is the only observable part of the `Dispose` contract, and `Pooling=False` is what makes
  it hold.)

### The flake itself

Cannot be asserted directly — it is probabilistic and needs parallel collections. The evidence it is
fixed is the same evidence round 5 used to characterise it: repeated full-suite runs. The change
description records a run count and the failure count (target: zero of *this* cause; the LiteDB
`BsonMapper` race is separate, documented, and out of scope).

### Gate

`dotnet build MyDogsbody.sln` clean; `dotnet test` green, zero skips, all four levels; the totals
move only by the regression-prevention tests added here.
