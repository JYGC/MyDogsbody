# Bugfix — the SQLite test harnesses clear the process-global connection pool

A standalone bugfix, found and measured during PR #18's review (rounds 4 and 5 of `invoice-extraction`)
and deferred there because the remedy touches ten test files that change does not. It is **change
#5.5** in spirit — a test-infrastructure fix with no user-visible behaviour — cut from `main`, not
from any `invoice-to-calendar` branch.

## Current Behavior (Defect)

WHEN a test harness that used a temp SQLite file finishes THEN it calls
`SqliteConnection.ClearAllPools()` before deleting the file — a **process-global** call that empties
*every* Microsoft.Data.Sqlite connection pool in the test process, not just its own.

WHEN xUnit runs two such harnesses' collections in parallel THEN one finishing can dispose a pooled
connection the other is mid-command on, and the second fails with:

```
System.ObjectDisposedException : Cannot access a disposed object.
Object name: 'SQLitePCL.sqlite3'.
   at System.Runtime.InteropServices.SafeHandle.DangerousAddRef(Boolean& success)
   at SQLitePCL.SQLite3Provider_e_sqlite3...sqlite3_prepare_v2(...)
   at Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader(CommandBehavior behavior)
```

WHEN the full suite is run THEN this surfaces intermittently — **measured at PR #18's review head
`c1beff0` over 45 full-suite runs: 5 failures, split between this cause and the separate LiteDB
`BsonMapper` race**. This cause was seen taking `TemplateApiFactoryTests.TestTemplate reports a rule
that found nothing as a sentence, not a union dump` and `SupplierApiFactoryTests.AddSupplier rejects a
name already taken and stores nothing more` (the latter surfacing as `Failed to retrieve all
suppliers` rather than the raw exception). 20 Integration-level-only runs produced none, consistent
with the cause: the collision needs the Contract / E2E / Startup SQLite collections running alongside.

WHEN a reviewer or contributor hits an intermittent failure of this shape THEN the guidance
(CLAUDE-project.md → *Build state*) is "do not re-run until it passes" — so a real regression in one
of these areas is indistinguishable from the flake until someone reads the stack trace.

## Expected Behavior (Correct)

WHEN a test harness that used a temp SQLite file finishes THEN it releases its own file handle and
deletes the file **without touching any other pool** — no process-global call.

WHEN `MyDogsbody.Database.DatabaseContextSetup.createDatabaseContext` opens its connection THEN it
does so with pooling disabled, so `context.Dispose()` closes the underlying handle immediately rather
than returning it to a pool that keeps the temp file locked on Windows.

WHEN a test builds a connection string for a temp database THEN it disables pooling on that string
too, so the FluentMigrator runner and any raw `SqliteConnection` the test opens release their handles
on dispose.

WHEN the full suite is run repeatedly THEN this cause produces zero failures — a temp file is either
deleted or, at worst, left in `%TEMP%` (a stray GUID-named file is cheaper than a cross-test flake),
and never by clearing a pool another test is using.

WHEN a harness is added later that uses a temp SQLite file THEN a test fails if it reintroduces
`SqliteConnection.ClearAllPools()`.

## Unchanged Behavior (Regression Prevention)

WHEN the application runs THEN `Startup.fs` still constructs exactly one `SqliteConnection` *object*
and holds it for the process lifetime. Its underlying handle is still opened and closed per store
operation, now with no pool behind it: measured at 0.090 ms per open/query/close cycle pooled vs
0.470 ms unpooled (Microsoft.Data.Sqlite 9.0.10, 2000 cycles) — +0.38 ms per operation, so under a
millisecond on a suppliers page load and nothing a user can observe.

WHEN `createDatabaseContext` hands out a connection THEN `PRAGMA foreign_keys` still reads back as `1`
(the `Foreign Keys=True` keyword is unaffected by `Pooling=False`).

WHEN the migrations run against a fresh temp file THEN they still produce the documented schema, and
every existing migration / store / API-factory / persisted-shape / contract / E2E test still passes,
unchanged in what it asserts.

WHEN a test disposes its `DatabaseContext` THEN the connection is still closed and unusable
afterward — `createDatabaseContext`'s `Dispose` contract is unchanged.

WHEN the suite is counted THEN the totals move only by the regression-prevention tests this change
adds; no existing test is deleted, and none is newly skipped.
