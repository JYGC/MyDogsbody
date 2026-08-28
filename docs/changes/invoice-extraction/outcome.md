# Outcome — Invoice extraction (change #4 of 7)

Branch `change/invoice-extraction`, cut from `main` at `9b0d4ce`.

## Status

**Code complete. Build clean. Suite green at all four levels, zero skips.** Two tasks in Phase 12
require running the application against the real 16 GB Thunderbird profile and are **pending the
maintainer** — see *Manual measurement* below. The immediate-rescan decision (Q1.9) stays
provisional until that measurement is taken.

## Test totals

Measured with `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` and `--filter "Level=..."`:

| Level | Count |
| --- | --- |
| Unit | 689 |
| Integration | 263 |
| Contract | 297 |
| E2E | 34 |
| **Total** | **1283** |

Baseline before this change was **1061** (`thunderbird-account-selection` head), so **+222**.

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

### Scope touched outside change #4

- `TemplateApiMappers.toFailingField` / `toFieldFailureReason` (change #2's file) extended for the
  11 new `InvoiceError` cases — their matches are deliberately exhaustive — plus the two
  reflection-counted contract tests.
- `MigrationSetup.rollbackToVersion` added (test-only).
- `MyDogsbody.Startup` gained a `ProjectReference` to `Integrations.Documents`.

## Manual measurement — REQUIRED before change #7 (Phase 12.4 / 12.5)

Not runnable from the test suite. Run the app against the real profile and record here:

```powershell
dotnet run --project MyDogsbody\MyDogsbody.csproj
```

Then, in the app:

1. **Settings → Mail accounts** — choose the profile folder, scan, select an account.
2. **Settings → Suppliers** — add the four measured suppliers (background.md → *The four worked
   templates*) with sender-domain matchers and payment terms.
3. **Settings → each supplier → Templates** — author the four templates. *(Note: the templates UI
   page — change #2's `2-6-ui` branch — is **not yet merged to `main`**; if it is still missing,
   seed `InvoiceTemplates` / `TemplateFieldRules` by hand or land that branch first.)*
4. **Invoices** — the initial scan runs on 14 days. Record:
   - **12.4 scan timing** — the first cold full scan; a second scan (watermarks in place); a
     window change from 14 → 180 days. **If a window change costs seconds, replace the immediate
     rescan with an explicit Refresh button** (the module creator already separates
     `GetInvoices` from `Scan` — wiring a Refresh is small).
   - **12.5 due-date coverage** — how many scanned invoices have a due date, and how many gain
     one once `DateFromField` + per-supplier payment terms are in the templates. The prediction
     is **12% → 39%**. **Take this number off the merged branch before starting change #7** — if
     it stays near 12%, the sync is not the highest-value next change (friction #19).

Delete `MyDogsbody.db`, `Thunderbird.db`, `Logging.db`, `Credentials.db` from the repo root after
the manual run.

### Timings

| Measurement | Result |
| --- | --- |
| First cold full scan | _pending_ |
| Second scan (watermarks) | _pending_ |
| Window change 14 → 180 | _pending_ |
| Due-date coverage, no `DateFromField` | _pending_ |
| Due-date coverage, with `DateFromField` | _pending_ |
| Immediate rescan kept? | _pending_ |
