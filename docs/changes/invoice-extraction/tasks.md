# Tasks — Invoice extraction

Change **#4 of 7**. Depends on **#1, #2, #3**. [`requirements.md`](requirements.md) ·
[`design.md`](design.md) · [decision record](../invoice-to-calendar/background.md)

**Branch: `change/invoice-extraction`, cut from `main` once #1, #2 and #3 have merged.** Everything in
this file lands on it, and it merges **only** when Phase 12 has passed in full — zero build errors,
zero test failures, zero skips, all four levels. No other change shares this branch, and none of this
work happens on `main`. **If this change is split** (see below), the piece that comes out gets its own
branch and its own gate — a split that stays on one branch has not been split.
See [background → *One branch per change*](../invoice-to-calendar/background.md#one-branch-per-change).

**The ordering rule, per task:** where a task produces production code, its unit test is written
first, run, and confirmed to fail *for the reason expected* before the implementation. Tasks marked
*(test-first)* carry production code.

**Reserved migration timestamps for this change: `20260810000004`–`20260810000008`.**
*(Renumbered from the originally reserved `20260809000005`–`…0009` per
[background → *Migration timestamps, reserved across the series*](../invoice-to-calendar/background.md#migration-timestamps-reserved-across-the-series):
change #1's after-the-fact `20260810000001` index migration and change #2's `…0002`–`…0003` both
sort above the old `20260809` block, so #4 is renumbered to stay monotonic. Table→timestamp map:
Invoices `…0004`, ScanProblems `…0005`, InvoiceTombstones `…0006`, ScanWindows `…0007`,
InvoiceSettings `…0008`.)*

---

## If this change gets too large

§4 named this the largest change after #2 and the first to split. **The designated split is the
scan-window apparatus: Phases 5 and 9.2, plus migrations `…0007` and `…0008`.** Those are two small
tables, a store, four small workflows and a settings page — all main-database machinery of the kind
change #1 already proved. Lifted out, they become `invoice-scan-windows`, and this change keeps a
fixed 14-day window until it lands.

They are *here* rather than in change #1 because a window with nothing to scan is a setting that does
nothing. Phases are ordered so the split can be taken cleanly at any point before Phase 10.

---

## Phase 0 — Rename `Integrations.Pdf` → `Integrations.Documents` (required)

Q1.14: one project per **capability**, not per library. Do this first, while the project still holds
one file.

- [x] **0.1** Delete `bin/` and `obj/` under `MyDogsbody.Integrations.Pdf` **before** attempting the
      move. *(The `logging-not-an-integration` change recorded that Windows refuses the directory
      rename while an IDE language server holds handles on the build output.)*
- [x] **0.2** `git mv` the project directory and rename the `.fsproj`. If the directory rename is
      refused, create the target and `git mv` file by file — history is preserved either way.
- [x] **0.3** Namespaces, `open`s, the `ProjectReference` in `MyDogsbody.Startup` and
      `MyDogsbody.Tests`, and the two `Project(...)` lines in `MyDogsbody.sln`.
      *Outcome:* the `.sln` diff is exactly two changed lines. If an IDE rewrites more, restore it.
- [x] **0.4** Rename `ActionNames.MyDogsbody.Integrations.Pdf.*` → `.Documents.*`.
- [x] **0.5** Gate: `dotnet build` clean, `dotnet test` green, **no test file edited**. The existing
      `PdfDocumentReader` contract suite must pass untouched.

## Phase 1 — The reading capability (required)

- [x] **1.1** *(test-first)* `DocumentSource` and `ReadDocumentText` added to
      `Domain/Documents/DocumentsTypes.fs`; `DocumentError` gains `DocumentFormatUnsupported` and
      `DocumentHasNoTextLayer`.
      *Outcome:* declared **beside** `ReadDocumentContent`, which is untouched (friction #7).
- [x] **1.2** Fixture documents in `Fixtures/Documents/`: a normal PDF; **a PDF with no text layer**;
      **a PDF that cannot be opened**; a `.docx`; a legacy binary `.doc`; an `.xlsx`; a plain-text
      attachment; a message with both `text/plain` and `text/html`; an HTML body whose values sit in
      table cells. **Synthetic, no real invoice content.**
- [x] **1.3** *(test-first)* `PdfDocumentReader.readText` — the same adapter now satisfying **both**
      capabilities.
      Tests: text extracted with block indices; **no text layer → `DocumentHasNoTextLayer`**; a
      corrupt file → `DocumentUnreadable`; `readContent` still behaves exactly as before.
      *Note:* 94.7% of measured PDFs have a text layer and only 1.6% do not — **no OCR**.
      *Depends on:* 1.1, 1.2, 0.5.
- [x] **1.4** *(test-first)* `WordDocumentReader.readText` — `.docx` only, via DocumentFormat.OpenXml.
      Tests: text extracted with paragraph block indices; **a legacy `.doc` returns
      `DocumentFormatUnsupported "doc"`, naming the format** (friction #8 — silence looks identical
      to "this supplier sends nothing").
- [x] **1.5** *(test-first)* `PlainTextDocumentReader.readText`. Tests: decoding; lines split; blank
      lines become block boundaries.
- [x] **1.6** *(test-first)* `EmailBodyReader.readText`.
      Tests: with both alternatives present, **the HTML is used** (*Finding 5*); block boundaries come
      from table cells and paragraphs, **not** from line breaks; with only plain text present, that
      is used; markup is stripped only when there is no structured alternative.
- [x] **1.7** *(test-first)* Format dispatch at the composition root — **by filename extension, never
      by declared content type**.
      Tests: a PDF declared `application/octet-stream` routes to the PDF reader; one declared
      `application/.pdf` does too; an `.xlsx` routes to no reader and yields
      `DocumentFormatUnsupported "xlsx"`.
      *Rationale:* 155 of 644 measured PDFs declare `application/octet-stream`.

## Phase 2 — Invoice types (required)

- [x] **2.1** *(test-first)* `InvoiceReference`, `Money`, `IssueDate`, `DueDate`, `InvoiceId` in
      `Domain/Invoices/InvoicesTypes.fs` (**extending** the file change #2 created).
      Tests: one accepted and one rejected value per rule with the reason; and
      **`InvoiceReference.create` folds internal whitespace** so `"1234 5678 90"` and `"1234567890"`
      are one value — reusing change #2's fold, not a second implementation.
- [x] **2.2** *(test-first)* `ScanWindowDays`, `ScanWindowId`, `StoredScanWindow`.
      Tests: 0 rejected; 3651 rejected with the bound named; 1 and 3650 accepted; `seeded` is exactly
      `[7; 14; 30; 90; 180]`; `fallback` is 14.
- [x] **2.3** `ValidInvoice`, `StoredInvoice`, `ScanProblemCause`, `ScanProblem`, `InvoiceTombstone`,
      `ScanResult`, the extra `InvoiceError` cases, and the sixteen new dependency function types.
      *Depends on:* 2.1, 2.2.
- [x] **2.4** *(test-first)* `GetCurrentTime` declared, and **its contract-suite rationale written
      into the test file as a comment** (friction #15).
      Tests: two successive calls non-decreasing; `Kind` as promised; the real clock within tolerance
      of `DateTime.Now`. *The arithmetic with actual logic is tested in 3.1.*

## Phase 3 — Pure workflows (required)

- [x] **3.1** *(test-first)* Cutoff arithmetic — a private pure function in
      `ScanForInvoicesWorkflow.fs`.
      Tests, with a **fixed clock**: 14 days back from a fixed date gives an exact date; **09:00 and
      17:00 on the same day give the same cutoff** (Q1.18); 180 days back across a year boundary;
      1 day and 3650 days.
- [x] **3.2** *(test-first)* `ResolveScanWindowWorkflow` — pure, total.
      Tests, all four rows of the table in `design.md`: nothing remembered → 14; remembered and
      present → that one; **remembered but since deleted → 14**; remembered deleted *and* 14 deleted
      → the shortest remaining. **The third and fourth are the cases nobody tries by hand.**
- [x] **3.3** *(test-first)* `ValidateInvoiceWorkflow` — pure.
      Tests: Ok with every field asserted; **a missing due date is Ok, not an error** (Q1.10); an
      invalid reference, amount or currency each returns its own case with the raw value carried.
- [x] **3.4** *(test-first)* `ScanMessageWorkflow` — flatten a mail message to a `ScannedMessage`.
      Tests: body and every attachment become parts with block indices; the subject is carried;
      **one unreadable attachment yields a problem cause and the other parts still arrive** (design
      decision 3); an attachment with no reader yields `FormatUnsupported` naming the format.
      *Depends on:* 1.7, 2.3.

## Phase 4 — The scan (required) — the orchestration

- [x] **4.1** *(test-first)* `ScanForInvoicesWorkflow`, **every dependency a lambda** — no mail
      store, no database, no files.
      Tests: a message yielding an invoice, every field asserted; `NoAccountSelected` short-circuits
      **with `readMailFolder` never called**; a message that yields nothing produces a problem **and
      the scan continues**; each of the eight problem causes is produced by its own case; two
      suppliers matching produces a problem naming **all** of them; the cutoff handed to
      `readMailFolder` is the one computed in 3.1.
      *Depends on:* 3.1, 3.3, 3.4.
- [x] **4.2** *(test-first)* Upsert and the natural key.
      Tests: rescanning an overlapping window **updates rather than duplicates**; two invoices from
      one message are both stored (the source message id is traceability, not the key); an invoice
      whose supplier has since been deleted becomes a problem rather than a row with no supplier.
- [x] **4.3** *(test-first)* Tombstones in the scan.
      Tests: a tombstoned key is **skipped**; removing the tombstone lets the next scan store it
      again.
- [x] **4.4** *(test-first)* Problem lifecycle.
      Tests: problems persist across scans; a message that later yields an invoice has its problem
      row **cleared**; a scan clears only the rows for messages it processed — **a narrower window
      does not erase diagnostics for messages outside it** (design decision 4).
- [x] **4.5** *(test-first)* `DeleteInvoiceWorkflow` and `UndeleteInvoiceWorkflow`.
      Tests: delete removes the row **and** writes a tombstone; `InvoiceNotFound` when it is already
      gone, **with no tombstone written**; undelete removes the tombstone; undeleting one that does
      not exist is reported, not silently ignored.

## Phase 5 — Scan windows *(the designated split point)*

- [x] **5.1** *(test-first)* `AddScanWindowWorkflow`. Tests: Ok; a duplicate refused with
      `ScanWindowAlreadyExists` carrying the days; out-of-bounds refused; the store **never called**
      on either refusal.
- [x] **5.2** *(test-first)* `DeleteScanWindowWorkflow`. Tests: Ok; **deleting the last one refused
      with `CannotDeleteLastScanWindow`** — a domain rule, not a UI guard; deleting a seeded window
      is allowed like any other.
- [x] **5.3** *(test-first)* `ListScanWindowsWorkflow` (ascending) and `SelectScanWindowWorkflow`
      (refuses a window not in the list, with the store never called).

## Phase 6 — Migrations (required)

- [x] **6.1** *(test-first)* `…0004_CreateInvoicesTable`. Tests: columns; **the unique index on
      `(SupplierId, Reference)` refuses a duplicate**; both foreign keys; `Down()` reverses it.
- [x] **6.2** *(test-first)* `…0005_CreateScanProblemsTable`. Tests: columns; index on
      `SourceMessageId`; `Down()`.
- [x] **6.3** *(test-first)* `…0006_CreateInvoiceTombstonesTable`. Tests: columns; unique index on
      `(SupplierId, Reference)`; `Down()`.
- [x] **6.4** *(test-first)* `…0007_CreateScanWindowsTable` — **with `Insert.IntoTable` seeding 7,
      14, 30, 90, 180** and a matching `Delete.FromTable` in `Down`.
      Tests: `Up` inserts exactly five rows; the unique index on `Days` refuses a sixth `14`;
      **`Down` removes the seeded rows**; **re-running migrations after a user deletes one does not
      restore it**.
      *Outcome:* **the first migration in this repository that carries data as well as structure
      (friction #17). Say so in the change description — the next person will copy whichever
      migration they open first.**
- [x] **6.5** *(test-first)* `…0008_CreateInvoiceSettingsTable`. Tests: the primary key is fixed at a
      single row and a second insert is refused; the setting column is nullable; `Down()`.

## Phase 7 — Stores (required)

- [x] **7.1** *(test-first)* Records and mappers for invoices, problems and tombstones.
      Tests: field-for-field both directions; **`ScanProblemCause` round-trips exhaustively** — one
      test per case, plus a reflection-driven test that fails if a case has no encoding.
- [x] **7.2** *(test-first)* `InvoiceStore.fs` — load, upsert, delete, tombstones, problems.
      Tests *(Integration)*: real temp SQLite; upsert on the natural key; the unique index as a
      backstop; problems written and cleared; tombstones round-trip.
      Tests *(Unit)*: each error path asserts its `ActionNames` string, message and inner exception.
- [x] **7.3** *(test-first)* `ScanWindowStore.fs` — windows and the remembered selection.
      Tests *(Integration)*: the seeded five are present after migration; add, delete; the selection
      persists as a **number**; a fresh database returns `None` for the selection.
- [x] **7.4** `ActionNames.MyDogsbody.Database.InvoiceStore.*` and `.ScanWindowStore.*`.

## Phase 8 — Composition root (required)

- [ ] **8.1** *(test-first)* `InvoiceApiMappers.fs` and `ScanWindowApiMappers.fs`.
      Tests: field-for-field both directions; **`ScanWindowUiType.Label` is composed by the mapper**,
      not by the component; each `InvoiceError` case → its intended action and message with the
      expected/unexpected split asserted.
- [ ] **8.2** *(test-first)* `InvoiceApiFactory` and `ScanWindowApiFactory`.
      Tests *(Integration)*: every member against a real temp database and the Phase 1 fixtures.
      No module-level I/O.
- [ ] **8.3** `ActionNames.MyDogsbody.Startup.InvoiceApi.*` and `.ScanWindowApi.*`.
- [ ] **8.4** `Startup.fs`: bind `GetCurrentTime` to `fun () -> DateTime.Now`, bind the four readers
      behind one `ReadDocumentText` dispatching on format, register both APIs.
      *Outcome:* `MainWindow.xaml.cs` unchanged.

## Phase 9 — UI (required)

- [ ] **9.1** `MyDogsbody.UI.Types`: `InvoiceUiType`, `ScanProblemUiType`, `TombstoneUiType`,
      `ScanWindowUiType` (`{ Id; Days; Label }`), `InvoiceApi`, `ScanWindowApi`,
      `Modules/InvoicesModule.fs`, `Modules/ScanWindowsBrowserModule.fs`.
- [ ] **9.2** *(split point)* `/settings/scan-windows` page: list, add, delete, the remembered one
      marked, **the last one's delete unavailable with its reason**.
- [ ] **9.3** *(test-first)* `ModuleCreators/InvoicesModuleCreators.fs`.
      Tests: selecting a window **persists then rescans** (write-then-reload); the initial value comes
      from `ResolveScanWindowWorkflow` through the API, **never from a literal 14 in a component**;
      a failure sets `ErrorAval` and a success clears it; no `Async.Start` in the file.
- [ ] **9.4** `Components/InvoicesComponents.fs`: the table, the **`MudSelect`** window picker bound
      to whatever the store holds, and the count and window stated above the table.
      *Note:* it cannot be a fixed `MudToggleGroup` — the number of windows is unknown at build time.
      The label says **what it measures**: "mail received in the last 90 days", not "90 days" (Q1.6).
- [ ] **9.5** Per-row delete with confirmation; an invoice with no due date shown **greyed with the
      reason it cannot go on a calendar**.
- [ ] **9.6** The problems view: sender, subject, date and cause per row.
- [ ] **9.7** The tombstones view with un-delete.
- [ ] **9.8** `Pages/InvoicesPage.fs` at `routeCi "/invoices"` — **top-level, not under settings** —
      registered in `Shell.fs`.

## Phase 10 — Contract suites (required)

- [ ] **10.1** One shared suite per new dependency function type — real adapter and every fake.
      **`ReadDocumentText` and `ReadDocumentContent` get separate suites** (friction #7).
- [ ] **10.2** `InvoiceApi` and `ScanWindowApi` contract suites: real records and every fake.
- [ ] **10.3** Persisted-shape tests for all five new tables.
- [ ] **10.4** Confirm no `ActionNames` entry remains under `Integrations.Pdf`, and the structural
      suite passes.

## Phase 11 — End to end (required)

- [ ] **11.1** `E2E/InvoicesFlowTests.fs` against a real temp SQLite file and the Phase 1 fixtures:
      a scan produces invoices; a window change persists and rescans; an invoice with no due date is
      greyed with its reason; a delete produces a tombstone and the row disappears; an un-delete
      restores it on the next scan; a problem row appears with sender and subject; a scan failure
      shows an alert with **exactly one entry logged**.
- [ ] **11.2** Confirm no test reaches `Startup.Startup`.

## Phase 12 — Gate (required)

- [ ] **12.1** `dotnet build MyDogsbody.sln` — zero errors.
- [ ] **12.2** `dotnet test` — zero failures, **zero skips**, all four levels. Record totals per level.
- [ ] **12.3** `Contracts/DomainIsolationTests.fs` and `AssertDomainReferencesNothing` still pass;
      `MyDogsbody.Domain` still names no Thunderbird, LiteDB, SQLite, MIME or PDF type.
- [ ] **12.4** **Measure a real scan** (friction #14, Q1.9). Record in `outcome.md`: the first cold
      full scan; a second scan with watermarks in place; a window change from 14 to 180 days.
      **If a window change costs seconds, replace the immediate rescan with an explicit Refresh
      button** — that is the condition Q1.9 was accepted under, and this is where it is settled.
- [ ] **12.5** **Record the real due-date coverage.** How many scanned invoices have a due date, with
      and without `DateFromField`. The measurement predicted 12% → 39%; friction #19 says change #7's
      value depends on it more than on anything in its own scope.
- [ ] **12.6** Confirm `MainWindow.xaml.cs` is untouched.

## Phase 13 — Documentation (required)

- [ ] **13.1** `CLAUDE-project.md`: `Integrations.Pdf` → `Integrations.Documents` in the structure
      table and the reference-direction notes; the five new migrations; the new domain workflows;
      **a sentence recording that migrations may now carry seed data**; the *Build state* totals.
- [ ] **13.2** `outcome.md`: totals per level, the scan timings from 12.4, the due-date coverage from
      12.5, and whether the immediate rescan survived.
- [ ] **13.3** Open `change/invoice-extraction` for review, with this file's checkboxes ticked and
      `outcome.md` on the branch. **Merge only after Phase 12 passed in full.**
      *Read 12.5's due-date number off the merged result before starting #7 — if it stays near 12%,
      the sync is not the highest-value next change (friction #19).*

---

## Optional

- [ ] **O.1** A "reprocess this supplier" action. The persisted problem rows make it cheap — they
      name exactly which messages to re-read after a template change, instead of a full pass over
      6.2 GB.
- [ ] **O.2** Mark a problem cause as "expected" so recurring non-invoices (council rates receipts,
      property-management owner statements — both measured, both parse cleanly, neither is an
      invoice) stop drawing the eye.
- [ ] **O.3** A reader for `.xlsx`. 114 measured against 1 `.docx`, but none of those sampled were
      invoices. The unsupported-format problem row names the format, so this can be revisited **from
      data** rather than from impression.
- [ ] **O.4** Show which template produced each invoice in the table. Already stored.
- [ ] **O.5** Server-side paging. Not needed at hundreds of invoices.

## Known risks carried into this change

- **Friction #14 / Q1.9 — immediate rescan over 6.2 GB.** Task 12.4 settles it with a measurement,
  and the fallback is already specified.
- **Friction #17 — a migration now carries data.** Task 6.4 and the documentation task both say so.
- **Friction #15 — the clock has no natural contract test.** Task 2.4 writes the rationale into the
  test file rather than leaving a gap.
- **Friction #7 — two "read a document" dependency types.** Separate contract suites, task 10.1.
- **Friction #8 — a silently skipped `.doc`.** Task 1.4 asserts the problem names the format.
- **Friction #19 — due-date coverage.** Task 12.5 records the real number.
- **The project rename may be refused by Windows.** Phase 0 carries the known workaround.
- **This is the largest change after #2.** The split is designated, and the phases are ordered for it.
