# Bugfix — the Thunderbird and Logging contexts share `BsonMapper.Global`, so two built at once can throw

The LiteDB `BsonMapper` first-use race. It is documented in CLAUDE-project.md → *Per-integration
databases*, captured in `invoice-extraction`'s `outcome.md`, and it has been met and deferred by
`invoice-extraction`, `sqlite-pool-flake`, `thunderbird-account-selection` and
`google-account-integration`, each time with the same note: *"a process-wide lock… wants its own change
folder"*. This is that change folder. (`credentials-per-provider` had already taken the Google store off
the shared mapper, so it is not affected.)

Re-found while re-running the suite after the three scratch projects were removed: in a clean checkout,
`ThunderbirdDatabaseContextModuleTests.getDatabaseContext exposes a working collection getter for every
one of the five entities` failed on the second of three runs with the documented signature. The same tree,
built incrementally, passed 8 runs in a row — which is exactly what an intermittent failure looks like, and
why the standing guidance ("do not re-run until it passes, note the test") has left it in place.

## Current Behavior (Defect)

WHEN two threads build a Thunderbird context for the first time in a process at the same moment THEN the
system can throw out of `ThunderbirdDatabaseContextModule.getDatabaseContext`, at its own warm-up line:

```
System.InvalidOperationException : Collection was modified; enumeration operation may not execute.
   at System.Collections.Generic.List`1.Enumerator.MoveNext()
   at System.Linq.Enumerable.ListWhereIterator`1.MoveNext()
   at LiteDB.BsonMapper.SerializeObject(Type type, Object obj, Int32 depth)
   at LiteDB.BsonMapper.Serialize(Type type, Object obj, Int32 depth)
   at LiteDB.BsonMapper.ToDocument(Type type, Object entity)
   at LiteDB.BsonMapper.ToDocument[T](T entity)
   at ...ThunderbirdDatabaseContextModule.getDatabaseContext(String databasePath, String connectionType)
      in ...\ThunderbirdDatabaseContextModule.fs:line 16
```

The cause is that every context maps its entities on the one process-wide `BsonMapper.Global` (a
`new LiteDatabase(connectionString)` with no mapper argument falls back to it too). Per CLAUDE-project.md's
diagnosis, that mapper publishes an entity's mapping before it has finished filling it, so a second thread
enumerates a member list the first is still adding to. The warm-up added while `architecture-compliance` was
written narrows the window but cannot close it: the warm-up *is* where the threads collide.

WHEN that is provoked directly — 8 threads released together to build a Thunderbird context, in a fresh
process (the mapper cache lives as long as the process, so each attempt has to be a new one) THEN the
system throws the exception above in **14 of 60 processes**.

WHEN the full suite runs THEN the system fails whichever test happens to build a context alongside another.
Earlier changes measured about 1 failing run in 6 to 1 in 14; this session saw 1 in 3 in a clean checkout and
0 in 8 in the incremental one. The test is incidental — `ThunderbirdDependencyContractTests`,
`MailAccountApiContractTests`, `ThunderbirdPersistedShapeTests` and now `ThunderbirdDatabaseContextModuleTests`
have each taken it.

WHEN a failure of this shape appears THEN a real regression in the same area cannot be told from the flake
without reading the stack trace, because the guidance is to note it and move on.

WHEN the Logging context is built THEN the system maps `ExceptionLog` on the same `BsonMapper.Global`. Same
structure, **not reproduced**: 0 of 60 processes at 8 threads and 0 of 60 at 32. It is in scope for the shared
cause, not for an observed failure — its warm-up maps one flat entity, so the window is far narrower, but
`writeLog` runs on whichever thread failed and the mapper is still shared.

## Expected Behavior (Correct)

WHEN any number of threads build Thunderbird and Logging contexts at the same moment THEN the system SHALL
return every one of them without throwing, each with every entity mapping fully built before it is handed out.

WHEN a Thunderbird or Logging context is built THEN the system SHALL give it a `BsonMapper` of its own, shared
with no other context, so that a first-use race between contexts is impossible by construction rather than
narrowed by a warm-up. This is what `GoogleDatabaseContextModule` already does.

WHEN a Thunderbird or Logging context is built THEN the system SHALL NOT map through `BsonMapper.Global` at
all — neither in its warm-up nor through the `LiteDatabase` it opens.

WHEN the full suite is run repeatedly THEN the system SHALL produce zero failures from this cause, and the
concurrent-build stress above SHALL produce zero failed processes at 8 and at 32 threads.

WHEN a context is added or changed later THEN a test SHALL fail if two contexts built on different files
share an entity mapping.

## Unchanged Behavior (Regression Prevention)

WHEN a Thunderbird or Logging document is written THEN the system SHALL CONTINUE TO persist the same
collection names and the same field names and values. The private mapper is a plain `BsonMapper()` — the same
default settings as `BsonMapper.Global` — so `TrimWhitespace` and `EmptyStringToNull` stay on for these two
stores (only the Google store turns them off, deliberately, for byte-exact secrets). A database file written by
an earlier build therefore still opens and reads unchanged: no migration and no data change. The persisted-shape
tests read raw documents through the untyped `GetCollection("Name")`, so they keep asserting what is stored
rather than what a mapper would produce.

WHEN the Google context is built THEN the system SHALL CONTINUE TO use its own mapper with `TrimWhitespace` and
`EmptyStringToNull` off — this change does not touch it.

WHEN a context is disposed THEN the system SHALL CONTINUE TO release its database file, so a test can delete
its temp file afterwards.

WHEN a caller builds a context THEN `getDatabaseContext databasePath connectionType` SHALL CONTINUE TO take and
return exactly what it does today: the context record types, the collection names and `Startup.fs` do not change.

WHEN the suite is counted THEN the totals SHALL move only by the regression tests this change adds; no test is
deleted and none is skipped.

## Exploratory measurement (not the change)

Before writing this, the remedy was tried in a throwaway worktree — never in this tree: each of the two modules
given a `BsonMapper()` of its own (warmed as before, and passed to `new LiteDatabase(...)`). The same stress then
threw in **0 of 60** processes at 8 threads and **0 of 60** at 32 for Thunderbird, and in 0 of 60 at each for
Logging; the full suite passed 8 runs in a row (1726 tests each). That measures the approach; it does not replace
the tests-first order in `tasks.md`, and `design.md` still has to choose the mechanism. It departs from the remedy
the earlier notes named — a process-wide lock — because it removes the shared state rather than guarding it, needs
no lock object visible to every integration (integrations reference nothing of each other), and cannot be
undone by a later first-use site forgetting to take the lock.

LiteDB is pinned at 5.0.21 in both projects, and `ILiteCollection<T>.EntityMapper` is public, so "two contexts do
not share an entity mapping" can be asserted deterministically — unlike the race itself, which needs a fresh
process per attempt.
