# Outcome — Invoice extraction (change #4 of 7)

Branch `change/invoice-extraction`, cut from `main` at `9b0d4ce`.

## Status

**Code complete. Build clean. Suite green at all four levels, zero skips.**

The Phase 12 measurement (2026-08-29) **found a blocking defect in change #3's `MailFolderReader`**:
it dropped any folder file over ~1 GiB silently, and the maintainer's invoice mail is in a 2.0 GB
Gmail INBOX. **Phase 14** replaced the whole-folder buffer with a streaming reader — done, green,
and the re-run confirms the 2.0 GB INBOX now reads (89,992 messages, was 0).

**12.4 is measured and settled:** a full re-read is ~60 s (58 s at 180 days, 63 s at 730 — the
cost is reading every folder, not the window), so the Q1.9 immediate-rescan is **dropped** for an
explicit "Scan now" — **Phase 15, done and green**. **12.5 is still open:** discovery mode yields
no invoices, so the due-date number needs `MeasureScan/Program.fs` supplier config and a
measurement-mode re-run (task 14.8b). Friction #19 read, now firm after a 730-day discovery run:
across ~100 K messages / 2 years, 2,026 were processed and **every one `NoSupplierMatched`** — the
four target suppliers barely appear, and real biller volume is ~a dozen senders, not the ~558-PDF
scale the 12→39% prediction assumed.

## Test totals

Measured with `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` and `--filter "Level=..."`:

| Level | Count |
| --- | --- |
| Unit | 698 |
| Integration | 264 |
| Contract | 297 |
| E2E | 35 |
| **Total** | **1294** |

Baseline before this change was **1061** (`thunderbird-account-selection` head), so **+233**.
Phase 14 (streaming reader) is **+7** over the 1283 the rest of the change reached; Phase 15
(window change reloads, "Scan now" scans) is a further **+4**.

### Known flakes (pre-existing category, not introduced here)

- **`ThunderbirdPersistedShapeTests` / any LiteDB-context test** — the documented `BsonMapper`
  first-use race (CLAUDE-project.md → *Per-integration databases*). Fails ~1 run in 6 under the
  full suite's parallelism, passes in isolation. Do not re-run to green; note the test.
- The SQLite store-test harnesses this change adds (`InvoiceStoreTests`, `ScanWindowStoreTests`,
  the contract and E2E harnesses) deliberately **do not** call
  `SqliteConnection.ClearAllPools()` — that process-global call was clearing *other* tests'
  pooled connections mid-use (two cross-test failures were traced to it during Phase 7). They
  leak a GUID-named temp file instead if the pool still holds a handle.

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

## Manual measurement — REQUIRED before change #7 (Phase 12.4 / 12.5)

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

### 12.4 — scan timing (re-run 2026-08-29, Phase 14 reader, all 10 accounts)

The 2.0 GB INBOX is now read: `outpost597100@gmail.com` reports **89,992 header-passing messages**
across 9 folders (was 0).

| Measurement | 180-day window | 730-day window |
| --- | --- | --- |
| First cold full scan (watermarks empty) | **58.3 s** (1,506 processed) | **63.1 s** (2,026 processed) |
| Second scan (watermarks in place, no re-read) | **5.3 s** | **5.7 s** |
| Full re-read (watermarks cleared — what a window widen costs) | **59.6 s** | **63.4 s** |

**Scan cost is ~60 s whatever the window** — 4× the window added ~5 s, because the cost is reading
and header-parsing every folder of every account, not the cutoff-filtered subset.

**Immediate rescan kept? No.** A window change forcing this is far past Q1.9's "~2 s", so the
fallback applies and is **built (Phase 15)**: a window change persists the choice and reloads the
stored ledger (`GetInvoices` / `GetProblems`); an explicit **"Scan now"** button reads the mailbox.

Exceptions logged: **0** on both runs. Cause breakdown (730-day): `NoSupplierMatched` 1,940
(discovery mode), `FormatUnsupported` 59, `AttachmentUnreadable` 27.

### 12.5 — due-date coverage (pending supplier config — task 14.8b)

Discovery mode produces **0 invoices** by construction (no suppliers → no templates → no
extraction), so the number needs `MeasureScan/Program.fs` → `suppliers` filled and a
measurement-mode re-run. **The 730-day discovery run makes friction #19 firm:** across ~100 K
messages / 2 years, **2,026 processed, every one `NoSupplierMatched`**. The sender table is
newsletters/research (`smartbrief.com` ×477, `outlook.com`/`hotmail.com` ×636, `physorg.com` ×115,
`arxiv.org` ×95); the billers present are low-count — IKEA ×10, OnePass ×13, HCF ×12, ahm ×6,
ATO ×4, VicRoads ×3, Hostplus ×2. The four target suppliers (IODM, Yarra Valley Water, Xero,
OC Energy) barely register — YVW appeared once at 180 days and not in the 730-day top 40. So a
measurement-mode run would extract a handful of invoices at most, and any coverage % is over
single-digit N. **The volume itself is the friction-#19 answer: this mailbox cannot validate the
12→39% prediction, so change #7's value has to be judged on the friction list, not a measured %.**

| Measurement | Result |
| --- | --- |
| Due-date coverage, no `DateFromField` | _pending — needs supplier config + measurement-mode re-run_ |
| Due-date coverage, with `DateFromField` | _pending — same_ |
