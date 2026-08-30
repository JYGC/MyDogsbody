# Outcome — Invoice extraction (change #4 of 7)

Branch `change/invoice-extraction`, cut from `main` at `9b0d4ce`.

## Status

**Code complete. Build clean. Suite green at all four levels, zero skips. Phases 0–17 done;
12.4 and 12.5 measured. In review — PR #18, seven review rounds so far.**

**Phases 16–17** were added during PR #18's review (rounds 3 and 6): a folder's watermark advances
past mail its scan never turned into an invoice, so an ordinary re-scan sees none of it.
**Phase 16** adds `ScanMode` and a "Rescan everything" action that clears the selected account's
watermarks first; **Phase 17** resets them when a scan aborts on a fatal error rather than leaving
them at EOF over unprocessed mail. Both are `ScanForInvoicesWorkflow` changes — see `design.md` →
*Decisions taken* #16 and #17.

The Phase 12 measurement (2026-08-29) **found a blocking defect in change #3's `MailFolderReader`**:
it dropped any folder file over ~1 GiB silently, and the maintainer's invoice mail is in a 2.0 GB
Gmail INBOX. **Phase 14** replaced the whole-folder buffer with a streaming reader — done, green,
and the re-run confirms the 2.0 GB INBOX now reads (89,992 messages, was 0).

**12.4 is measured and settled:** a full re-read is ~60 s across three runs (58 s at 180 days,
62–66 s at 730 — the cost is reading every folder, not the window), so the Q1.9 immediate-rescan
is **dropped** for an explicit "Scan now" — **Phase 15, done and green**.

**12.5 is measured and closed as "not meaningfully answerable from this mailbox."** A
measurement-mode run with three token suppliers extracted **0 invoices** — the pipeline ran (8
messages matched a supplier, 6 reached template evaluation) but every guessed template rule found
nothing, and there are only ~6 plausible invoices in 2 years anyway. Friction #19's due-date
number needs the maintainer to author templates against real invoice PDFs; the ~558-PDF scale the
12→39% prediction assumed is not present here.

## Test totals

Measured with `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` and `--filter "Level=..."`:

| Level | Count |
| --- | --- |
| Unit | 738 |
| Integration | 274 |
| Contract | 299 |
| E2E | 37 |
| **Total** | **1348** |

Baseline before this change was **1061** (`thunderbird-account-selection` head), so **+287**.
Phases 0–15 reached **1294**; PR #18's six review rounds added **+29** (see the round notes below);
Phases 16–17 (watermark control — "Rescan everything", fatal-abort reset) add a further **+9**
(6 workflow unit, 1 module-creator unit, 1 factory integration, 1 E2E); round 7 adds **+6**
(5 workflow unit, 1 factory integration); round 9 adds **+10** (6 unit, 2 integration, 1 contract
theory over both implementations).

### Known flakes (pre-existing category, not introduced here)

- **`ThunderbirdPersistedShapeTests` / any LiteDB-context test** — the documented `BsonMapper`
  first-use race (CLAUDE-project.md → *Per-integration databases*). Fails ~1 run in 6 under the
  full suite's parallelism, passes in isolation. Do not re-run to green; note the test.

  **Captured** (PR #18 review round 4, 8 consecutive `--filter "Level=Contract"` runs at the
  reviewed head and 8 more at the commit before it — 3 failures and 1 failure respectively, so it
  is reproducible at the Contract level alone and is *not* introduced by any review commit). Same
  exception every time, thrown **at the warm-up line itself**, in
  `ThunderbirdDatabaseContextModule.fs` line 15 or 16 — not the `CredentialsDatabaseContextModule`
  the CLAUDE-project.md note records:

  ```
  System.InvalidOperationException : Collection was modified; enumeration operation may not execute.
     at System.Collections.Generic.List`1.Enumerator.MoveNext()
     at System.Linq.Enumerable.ListWhereIterator`1.MoveNext()
     at LiteDB.BsonMapper.SerializeObject(Type type, Object obj, Int32 depth)
     at LiteDB.BsonMapper.Serialize(Type type, Object obj, Int32 depth)
     at LiteDB.BsonMapper.ToDocument(Type type, Object entity)
     at LiteDB.BsonMapper.ToDocument[T](T entity)
     at ...ThunderbirdDatabaseContextModule.getDatabaseContext(String, String)
        in ...\ThunderbirdDatabaseContextModule.fs:line 16
  ```

  Three different tests were seen taking it, all of them the *first* thing their fixture does —
  whichever one happens to construct a context concurrently with another:
  `ThunderbirdDependencyContractTests.a saved selection is visible to a later load, and clearing it
  persists as absent(implementation: "real adapter")` (via `withTempContext`, line 37),
  `MailAccountApiContractTests.ScanForAccounts against the committed fixture finds ten accounts, and
  GetAccounts sees them(implementation: "real api")` (via `withRealApi`, line 24), and
  `ThunderbirdPersistedShapeTests.the five collections are named exactly as documented, and only
  those five exist`. The test is incidental; the shared `BsonMapper.Global` is the subject. This
  confirms CLAUDE-project.md's own diagnosis and its stated remedy — a process-wide lock around the
  warm-up, or one static initialiser warming every entity under a single lock — which wants its own
  change folder and is **not** attempted here.
- The SQLite store-test harnesses this change adds (`InvoiceStoreTests`, `ScanWindowStoreTests`,
  the contract and E2E harnesses) deliberately **do not** call
  `SqliteConnection.ClearAllPools()` — that process-global call was clearing *other* tests'
  pooled connections mid-use (two cross-test failures were traced to it during Phase 7). They
  leak a GUID-named temp file instead if the pool still holds a handle.

  **The hazard is still live, and this is the second known flake — captured in PR #18 review round
  5.** Seven *older* harnesses still call `ClearAllPools()`: `SupplierApiContractTests`,
  `SupplierDependencyContractTests`, `SupplierPersistedShapeTests`, `DatabaseContextSetupTests`,
  `MigrationTestHelpers`, `SupplierStoreTests` and `TemplateStoreTests`. xUnit runs their
  collections in parallel, so one of them finishing disposes a pooled connection another is
  mid-command on:

  ```
  System.ObjectDisposedException : Cannot access a disposed object.
  Object name: 'SQLitePCL.sqlite3'.
     at System.Runtime.InteropServices.SafeHandle.DangerousAddRef(Boolean& success)
     at SQLitePCL.SQLite3Provider_e_sqlite3...sqlite3_prepare_v2(...)
     at Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader(CommandBehavior behavior)
  ```

  **Measured at the untouched review head `c1beff0`: 45 full-suite runs produced 5 failures**, of
  *both* kinds — this one taking `TemplateApiFactoryTests.TestTemplate reports a rule that found
  nothing as a sentence, not a union dump` and `SupplierApiFactoryTests.AddSupplier rejects a name
  already taken and stores nothing more` (which surfaces it as `Failed to retrieve all suppliers`
  rather than as the raw exception), and the `BsonMapper` one above taking
  `ThunderbirdStoreTests.loadProfileRoot returns None for a fresh database` and
  `MailAccountApiFactoryTests.ScanForAccounts reports NoProfileFound as an unlogged exception when
  the folder has no prefs.js`. 20 Integration-level-only runs produced none, which is consistent
  with the cause: the collision needs the Contract/E2E/Startup SQLite tests running alongside.
  Both flakes are therefore **pre-existing at the reviewed head and introduced by no review
  commit**. The remedy — dropping `ClearAllPools()` from those seven harnesses, as the new ones
  already do — touches files this change does not, and wants its own change folder.

### Review round 6 — a failed read inside a composite load set no alert

`InvoicesModuleCreators` performs three or four API reads per load and matched each one on its
own, discarding all but one error with `| Error _ -> ()`. So a read that failed while the
operation the user actually asked for succeeded reported **nothing**:

| Load | Reads | Reported before |
| --- | --- | --- |
| `scan` (initial load, "Scan now") | `Scan`, `GetInvoices`, `GetProblems`, `GetScanWindows` | `Scan` only |
| `loadLedger` (window change, after a delete) | `GetInvoices`, `GetProblems`, `GetScanWindows` | `GetInvoices` only |
| `getScanWindowsBrowserModule.load` | `GetScanWindows`, `GetSelectedScanWindow` | `GetScanWindows` only |

The ledger read is the one that costs. The initial page load *scans*, so a `GetInvoices` failure
(a stored row `InvoiceStore.orRaise` refuses, a locked database) opened the page on an empty table
under a `0 invoice(s), mail received in the last 14 days` count line with no alert — the invoices
were stored, unreadable, and the page said there were none. All three are now one
`firstFailure` list, ordered so the operation the user pressed still wins.

Six failing-first unit tests, four of them red for the predicted reason (`ErrorAval` was `None`
where a message was expected). Two of the six cover `getScanWindowsBrowserModule`, which had **no
test of any level** before this round.

### Round 6's deferral, and round 3's — the watermark can advance past mail nothing extracted (fixed: Phases 16–17)

Two review findings, one root cause. `MailFolderReader.readFolder` saves each folder's watermark
as part of `read`, and `resumeOffset` skips a message older than the cutoff *before parsing its
body*, so a folder can advance to EOF having produced no invoice:

- **Round 3, finding 2.** A user opens `/invoices` before configuring any supplier: every folder
  is read, matched against nothing, and its watermark advanced to the end. They then add a
  supplier and template — and an ordinary scan resumes from EOF and sees none of that supplier's
  mail. The recovery (`MailAccountApi.ClearWatermarks`, per account) existed on the mail-accounts
  page but the invoices page never surfaced it.
- **Round 6.** `scanForInvoices` calls `read` — which commits the watermarks — *before* it
  processes a single message. A fatal error part-way through (`loadTemplatesForSupplier` /
  `upsertInvoice` store failure → `ScanAcc.Fatal`) leaves every watermark at EOF over unprocessed
  mail. No invoice, no problem, nothing on screen.

**Fixed** — folded into this change rather than a follow-up (the user's call). `scanForInvoices`
gains `clearWatermarks: ClearWatermarks` (borrowed from the MailAccounts area) and `mode: ScanMode`:

- **Phase 16** — `FullRescan` clears the selected account's watermarks before reading.
  `InvoiceApi.RescanEverything`, a "Rescan everything" button beside "Scan now". `Scan` and the
  initial load stay `IncrementalScan`.
- **Phase 17** — the `ScanAcc.Fatal` branch calls `clearWatermarks accountId` and returns the
  original fatal error regardless (`Result.mapError (fun _ -> error)`), so the next scan re-reads
  the account rather than skipping past the unprocessed mail. Chosen over recording a separate
  "last processed" offset (two offset/cutoff pairs on the persisted `ScanWatermarkEntity` and
  "which is authoritative" logic in `resumeOffset` — materially more surface, more persisted-shape
  churn, for a rare path). Cost: one ~60 s full re-read on the next scan after a fatal error,
  which is the same cost as the first scan ever and only after the store is fixed. `design.md` →
  *Decisions taken* #16 and #17.

### Review round 7 — Phase 17 reset only one of five aborts, and 16.3's integration test was missing

The first cold read of the Phases 16–17 commit (`c568108`). Two findings, both self-found; **0**
open review comments at the start of the round and 0 at the end.

1. **`readMailFolder`, not `ScanAcc.Fatal`, is the line that advances the watermarks.** Phase 17
   wrapped only the `Fatal` branch, so the four *other* steps that sit after `readMailFolder` —
   `loadSuppliers`, `loadTombstones`, `saveScanProblems`, `clearScanProblems` — still aborted over
   watermarks already at EOF, which is the same defect the phase was written to close.
   `loadSuppliers` is the worst of the four: it fails with **every** message unprocessed, and the
   next scan sees none of that mail. `saveScanProblems` is the quietest: the invoices are stored,
   but the diagnostics are lost and can only be re-derived by reading mail that is now marked
   "already read", so the problem list stays empty for it for good. requirements.md already
   covered all of them — "WHEN a scan aborts on a fatal error (a store or reader failure, not a
   per-message problem) … SHALL NOT advance them past mail it read but never turned into an
   invoice or a problem" — so this was spec drift as well as a defect. Fixed by
   `resettingWatermarksOnError`, applied to all five sites; `readMailFolder`'s own failure is
   deliberately **not** wrapped, because nothing was read. Four failing-first unit tests, each red
   for the predicted reason (`clearWatermarks` spy empty), plus a fifth locking in that a
   `clearWatermarks` failure on the new path does not replace the original error.
2. **Task 16.3's second integration test did not exist** although the task was ticked: "with a
   seeded account + watermark row, the row is gone afterward". The only behaviour
   `RescanEverything` adds over `Scan` — that it deletes the account's watermark rows — had no
   coverage above the domain unit level, so nothing proved the factory's `clearWatermarksForAccount`
   adapter was bound to the *watermarks* collection at all. Added, with a `Scan`-leaves-the-row
   contrast in the same test; verified non-vacuous by temporarily wiring `RescanEverything` to
   `IncrementalScan`, which turns it red.

### Review round 9 — `SupplierGone` had a branch, a message and a test, and no producer

Round 8 was terminated by an API usage limit part-way through and left an uncommitted, unverified
diff behind; round 9 re-derived the finding from scratch, reproduced it red at HEAD, and kept it.
**0** open review comments at the start of the round and 0 at the end.

1. **`InvoiceApiFactory.upsertInvoice` could not produce `SupplierGone`, so the one non-fatal
   upsert failure was fatal.** `scanForInvoices` reads `loadSuppliers` once and matches every
   message against that snapshot, so a supplier deleted while the scan runs (12.4 measured a scan
   at ~60 s) still matches, and the upsert hits the `Invoices → Suppliers` foreign key. The
   factory mapped every store failure through `toInvoiceError`, so it arrived as
   `InvoiceStoreFailed` — and `ScanForInvoicesWorkflow.step` has exactly two upsert-failure
   branches: `SupplierGone` records one problem for that message and carries on, *everything else*
   sets `ScanAcc.Fatal`. So one deleted supplier ended the whole run, discarded every problem
   gathered so far, and reset the account's watermarks. requirements.md asks for the opposite:
   "WHEN a scan finds an invoice whose supplier has since been deleted THE SYSTEM SHALL report it
   as a problem rather than storing an invoice with no supplier." Spec drift as well as a defect,
   and the `SupplierGone` branch plus its workflow unit test were reachable only from tests.

   Measured red at HEAD, both implementations of the shared contract suite, before the fix:
   `real` → `expected SupplierGone, got InvoiceStoreFailed "Failed to store invoice."`;
   `fake` → `expected the write to be refused - there is no supplier 2` (the fake accepted a
   supplier id the real store's foreign key refuses — the drift a shared suite exists to catch).
   Fixed by `InvoiceStore.isMissingSupplier` + one branch in the factory. design.md → *Decisions
   taken* #13.

2. **The new predicate's true path had no unit coverage** — only the two integration tests, which
   cannot vary the exception shape. `isMissingSupplier` is a new pure function and CLAUDE.md asks
   for the success path in isolation, so five unit cases were added over hand-built exception
   chains: a violation at three depths, a **unique** violation (extended 2067, primary 19 — the
   same primary code) staying false, and one where the foreign-key violation is not the first
   entry in an `AggregateException`. Verified non-vacuous by mutation: reading `SqliteErrorCode =
   19` instead of the extended code turns exactly the unique-violation case red, and deleting the
   `AggregateException` branch turns exactly the sibling case red. (The three depth cases are
   shape documentation, not discriminating — `AggregateException.InnerException` is
   `InnerExceptions[0]`, so the generic chain walk already covers a single-inner aggregate.)

**E2E for finding 1 is deliberately absent, and that is a harness gap, not an oversight.** The
defect needs the supplier row to exist when `loadSuppliers` runs and to be gone when
`upsertInvoice` runs — an interleaving inside one `InvoiceApi.Scan` call, which the black-box
bUnit harness (`withInvoicesHarness`) has no seam to produce. The workflow half is covered at
Unit, the binding half at Contract against the real store.

**Deferred, out of scope — the main database's single shared `SqliteConnection` is not safe under
the concurrent use this change makes reachable.** `DatabaseContextSetup.createDatabaseContext`
constructs one `SqliteConnection` per process and every store function does `Open()` … `Close()`
on that one instance, so two overlapping operations corrupt each other's transaction state. A
throwaway probe (two threads, 40 upserts each, same `DatabaseContext`) failed **80 of 80** with
`SQLite Error 1: 'cannot rollback - no transaction is active'`. It is **pre-existing** — the same
shape as `SupplierStore` and `TemplateStore` from changes #1 and #2 — but this change widens the
window from "a double-clicked Save" to "any main-database write during a ~60 s background scan",
since every module creator runs its work through `Async.Start` and the settings pages stay live
throughout. Fixing it means changing how the main database hands out connections (a fresh one per
call, or a serialising lock) and touches every store above it, so it wants its own change folder —
suggested `docs/changes/main-database-connection-sharing/` as a bugfix — rather than a review
round.

## What landed

- **Phase 0** — `Integrations.Pdf` → `Integrations.Documents` (one project per capability).
- **Phase 1** — four document readers behind `ReadDocumentText` + `DocumentFormat.ofFileName` +
  `DocumentReaders.dispatch`; new packages `DocumentFormat.OpenXml`, `HtmlAgilityPack`.
- **Phase 2** — every invoice/scan-window constrained type, 11 new `InvoiceError` cases, 15
  dependency function types, `GetCurrentTime` + its friction-#15 contract suite.
- **Phase 3–5** — `computeCutoff`, `ResolveScanWindowWorkflow`, `ValidateInvoiceWorkflow`,
  `ScanMessageWorkflow`, `ScanForInvoicesWorkflow`, delete/undelete, scan-window CRUD.
- **Phase 6** — five migrations `20260810000004`–`…0008` (**renumbered** from the reserved
  `20260809…` block per background.md). `…0007` is the repo's first seed-data migration.
- **Phase 7** — `InvoiceStore`, `ScanWindowStore`, `InvoiceRecordMappers`;
  `ScanProblemCause` round-trips exhaustively.
- **Phase 8** — `InvoiceApiFactory`, `ScanWindowApiFactory`, mappers; `Startup.fs` registers
  both APIs; `MainWindow.xaml.cs` **untouched** (verified: `git diff origin/main -- MyDogsbody/`
  is empty).
- **Phase 9** — `/invoices` (top-level) and `/settings/scan-windows` pages; module creators;
  `InvoicesComponents` / `ScanWindowsComponents`; "Scan windows" nav link.
- **Phase 10** — separate contract suites for `ReadDocumentText` and `ReadDocumentContent`
  (friction #7); a shared suite for the store-backed dependency types (real adapter + fake);
  persisted-shape tests for all five tables; `ActionNames` no-`.Integrations.Pdf.` check.
- **Phase 11** — `E2E/InvoicesFlowTests`: seeded invoice renders, due-date-less greyed, delete →
  tombstone → row gone → un-delete, problem row renders, window picker bound to the store,
  no-account scan → alert + nothing logged, unreachable store → alert + logged.
- **Phase 15** — Q1.9 fallback (12.4 measured ~60 s): `InvoicesModuleCreators.selectWindow` and
  `deleteInvoice` now `loadLedger` (`GetInvoices` / `GetProblems`, no `InvoiceApi.Scan`);
  `InvoicesComponents` gained a **"Scan now"** button bound to `Rescan`, disabled while busy;
  `undeleteInvoice` still scans (only a scan restores a hard-deleted row). `InvoiceApi` /
  `InvoicesModule` doc comments corrected. Tests: module-creator (select/delete reload not scan,
  narrowing re-queries, Rescan/undelete scan), E2E (window change filters the ledger with no
  scan and no alert; "Scan now" reads the mailbox).
- **Phase 14** — `MailFolderReader` streaming rewrite (forced by the Phase 12 measurement).
  `bufferSpan` / `splitIntoMessages` / `processSegment` gone; `segmentStartOffsets` (byte-scan for
  `From ` boundaries), `foldMboxSegments` (streams `StreamChunkBytes` = 4 MiB at a time, one
  segment per `onSegment` call, memory bounded by one chunk + one message), `normalizeStartOffset`
  in. `MaxBufferableBytes` is now a per-segment ceiling; `MaxMessageBytes` (128 MiB) bounds an
  in-progress segment before the reader byte-scans forward for the next boundary. `classifySegment`
  keeps the cutoff / torn-message / false-boundary rules verbatim. Watermarks, CRLF, incremental
  reads all unchanged; the resume seam (`\nFrom ` at the buffer start) is trimmed in the fold.
  `countMessages` stops over-counting a separator-less non-last fragment.

### Design deviations from the specs (all recorded in `design.md`)

1. Migrations renumbered to `20260810000004`–`…0008`.
2. The four `readText` readers return `DocumentError` directly (no `ActionName`) — `MailFolderReader` precedent.
3. `EmailBodyReader` only parses HTML; the plain-vs-HTML choice is one line in `ScanMessageWorkflow`.
4. Constrained date types are `InvoiceIssueDate` / `InvoiceDueDate` (a same-named type shadows the `TargetField` union cases `IssueDate` / `DueDate` in the composition root).
5. `UpsertInvoice` is per-invoice, not the batch `UpsertInvoices` the design listed — "continue past a failure and report per row" needs it.
6. `ScanResult` carries what *this scan* did; the page's full view comes from `GetInvoices` / `GetProblems`.
7. `ValidInvoice` gained `MessageReceivedAt` (the window filter is measured on mail-received date, which the invoice must therefore carry).
8. "Two invoices from one message" is verified at the store layer (the engine produces one per message; the table key is `(SupplierId, Reference)`).
9. `SupplierGone` reuses the `NoSupplierMatched` problem cause rather than adding a ninth.
10. `MailFolderReader` gets a streaming mbox reader (Phase 14). Not in the original scope —
    forced by the Phase 12 measurement finding that change #3's reader drops any folder over
    ~1 GiB silently, which hid the maintainer's entire invoice history. design.md → *Decisions
    taken* #14.
11. A window change reloads the ledger; a scan is the explicit "Scan now" (Phase 15). The Q1.9
    fallback, triggered by 12.4's ~60 s measurement. design.md → *Decisions taken* #15.

### Scope touched outside change #4

- `TemplateApiMappers.toFailingField` / `toFieldFailureReason` (change #2's file) extended for the
  11 new `InvoiceError` cases — their matches are deliberately exhaustive — plus the two
  reflection-counted contract tests.
- `MigrationSetup.rollbackToVersion` added (test-only).
- `MyDogsbody.Startup` gained a `ProjectReference` to `Integrations.Documents`.
- **`MyDogsbody.Integrations.Thunderbird/MailFolderReader.fs`** (change #3's file) — the streaming
  rewrite, Phase 14. See *Decisions taken* #14. `thunderbird-account-selection/outcome.md` → **O.5**
  is marked done and points here.
- `.gitignore` — `*.db` added (Phase 14): the `MeasureScan` commits had checked in four runtime
  databases, one holding real Thunderbird account data. Untracked now.

## Manual measurement — DONE (Phase 12.4 / 12.5, 2026-08-29)

Not runnable from the test suite. **`MeasureScan/` is a throwaway harness that does it** — it uses
the real composition root (real migrations, `MailAccountApi` / `SupplierApi` / `TemplateApi` /
`InvoiceApi`, real `MailFolderReader` against the real profile), seeds the four measured suppliers
+ templates, scans three times with a `Stopwatch`, and prints a table to paste below. See
`MeasureScan/README.md` — **it is not in `MyDogsbody.sln`, so the gate never touches it**; delete
it and the root `.db` files once the numbers are in.

```powershell
# from the repo root
dotnet run --project MeasureScan\MeasureScan.fsproj      # step 1: prints your accounts
# edit MeasureScan/Program.fs: accountEmail + the four suppliers' domain / term / date-format
dotnet run --project MeasureScan\MeasureScan.fsproj      # step 3: measures, prints the table
```

The `12% → 39%` prediction was over ~30 suppliers / 558 PDFs; four suppliers won't reproduce that
scale, so the number is "of what these four templates extracted, X% carry a due date". **Take it
off the merged branch before starting change #7** — if it stays near 12%, the sync is not the
highest-value next change (friction #19).

*(The templates UI — change #2's `2-6-ui` branch — is still not merged; the harness sidesteps it
by seeding through `TemplateApi`, which is wired. Merging `2-6-ui` and clicking through the app is
the alternative if you want the numbers from the real UI path.)*

Cleanup: `Remove-Item measure.db, Thunderbird.db, Logging.db -ErrorAction SilentlyContinue` and
`Remove-Item -Recurse MeasureScan`.

### First run (2026-08-29) — surfaced the Phase 14 defect

`MeasureScan` in discovery mode found the invoice mail was **not in the scan at all** — the reader
dropped `outpost597100@gmail.com`'s 2.0 GB INBOX silently. Phase 14 fixed that; the re-run below is
against the streaming reader.

### 12.4 — scan timing (2026-08-29, Phase 14 reader, all 10 accounts)

The 2.0 GB INBOX is now read: `outpost597100@gmail.com` reports **89,992 header-passing messages**
across 9 folders (was 0).

| Measurement | 180 d | 730 d (discovery) | 730 d (3 suppliers) |
| --- | --- | --- | --- |
| First cold full scan (watermarks empty) | **58.3 s** | **63.1 s** | **62.6 s** |
| Second scan (watermarks in place, no re-read) | **5.3 s** | **5.7 s** | **5.2 s** |
| Full re-read (watermarks cleared — a window widen) | **59.6 s** | **63.4 s** | **61.0 s** |

**Scan cost is ~60 s whatever the window or supplier count** — 4× the window added ~5 s, because
the cost is reading and header-parsing every folder of every account, not the cutoff-filtered
subset or the matching.

> **The third row's parenthetical was a claim the code did not implement, and PR #18's round-2
> review closed the gap.** `MeasureScan` produced that row by calling `ClearWatermarks` by hand;
> the scan path had no equivalent, because a watermark was keyed on the folder's size and
> modification time alone — neither of which a longer scan window changes. So widening 7 days to
> 180 and pressing "Scan now" resumed from the stored offset and answered "nothing new" for the
> older mail the wider window was asking for, silently: messages older than the previous cutoff
> are skipped by `classifySegment` before their body is ever parsed, so they had been passed
> over, not read. `FolderWatermark` now also records the cutoff its offset was reached under
> (`ScanWatermarkEntity.CutoffTicks`, an int64 for the same round-trip reason as
> `ModifiedAtTicksUtc`), and `MailFolderReader.resumeOffset` restarts at 0 when the cutoff being
> asked for is earlier than the recorded one. A window that merely slides forward a day, or
> narrows, still resumes — the 5.3 s second scan above is unaffected. A widen now costs the ~60 s
> this row measured, automatically rather than by hand.

**Immediate rescan kept? No.** A window change forcing this is far past Q1.9's "~2 s", so the
fallback applies and is **built (Phase 15)**: a window change persists the choice and reloads the
stored ledger (`GetInvoices` / `GetProblems`); an explicit **"Scan now"** button reads the mailbox.

Exceptions logged: **0** on both runs. Cause breakdown (730-day): `NoSupplierMatched` 1,940
(discovery mode), `FormatUnsupported` 59, `AttachmentUnreadable` 27.

### 12.5 — due-date coverage (measurement-mode run, 3 token suppliers, 730-day window)

`MeasureScan/Program.fs` → `suppliers` was filled with the three that appear as **direct biller
mail** in the discovery dump — InkStation (`inkstation.com.au`, "Your InkStation Tax Invoice"),
"Plumbing Bros" (subject "Invoice I…", a Xero-generated invoice), HCF (`hcf.com.au`, premium
notices) — with best-effort templates (the real PDF/body layouts are unknown).

**Result: 0 invoices extracted, 0 with a due date — coverage undefined (0/0).**

The cause breakdown is the informative part — the engine ran end to end:

| Cause | Count | Meaning |
| --- | --- | --- |
| `NoSupplierMatched` | 1,932 | (was 1,940 — so **8 messages did match a supplier**) |
| `RuleFoundNothing` | 6 | matched a supplier **and** a template, but the guessed labels ("Total (inc GST)", "Amount Due", "Invoice Date") found nothing in the real document |
| `NoTemplateMatched` | 2 | matched a supplier but the message had no part the template targets |

So the pipeline works — suppliers matched, templates applied — and **0 came out because the
templates are guesses.** A real coverage number needs a human to open an actual invoice PDF from
this mailbox and author the rules against its labels. And the ceiling is low: **8 supplier-matched
messages over 2 years, ~6 plausible invoices**, against the ~558-PDF / ~30-supplier scale the
12→39% prediction was made at.

**Friction #19, answered:** this mailbox cannot produce a meaningful due-date-coverage figure —
too few invoices, and extracting them needs template authoring that only the maintainer can do.
Change #7's value has to be judged on the friction list and the `DateFromField` mechanism itself
(which *is* built and unit-tested), not on a measured % from here.

| Measurement | Result |
| --- | --- |
| Due-date coverage, no `DateFromField` | **0 / 0** — no invoice reached extraction (templates are guesses; 6 `RuleFoundNothing`) |
| Due-date coverage, with `DateFromField` | **0 / 0** — same |
