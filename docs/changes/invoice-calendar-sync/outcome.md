# Outcome — Invoice calendar sync (change #7 of 7)

Branch `change/invoice-calendar-sync`, cut from `origin/main` after changes #4 (`invoice-extraction`)
and #6 (`google-account-integration`) had both merged. This is the last change in the
invoice-to-calendar series and the only one that can destroy data outside the application.

## Gate results (Phase 11)

**Build:** `dotnet build MyDogsbody.sln` — **0 errors**, 3 warnings, all three pre-existing and
unrelated to this change (`PdfProcessing/Program.fs` FS0025, `PdfDocumentReaderTests.fs` FS0760,
`ScanWindowStoreTests.fs` FS0020 — present on `main` before this branch started).

**Tests, per level** (`dotnet test --filter "Level=..."`, zero skips throughout):

| Level | Passed | Failed | Total |
| --- | --- | --- | --- |
| Unit | 872 | 1 | 873 |
| Integration | 360 | 0 | 360 |
| Contract | 419 | 0 | 419 |
| E2E | 51 | 0 | 51 |
| **Full suite** | **1702** | **1** | **1703** |

The one Unit-level failure is `Database/SqliteConnectionPoolingTests.fs`'s
`` `every SQLite connection string a test builds disables pooling` ``, reporting the same 10
offending lines this change's own baseline measurement of `main` found before any work started
here (`InvoiceDependencyContractTests.fs` ×2, `InvoicePersistedShapeTests.fs`, `InvoiceStoreTests.fs`,
`ScanWindowStoreTests.fs`, `E2E/InvoicesTestHarness.fs`, `Startup/InvoiceApiFactoryTests.fs` ×4,
`Startup/ScanWindowApiFactoryTests.fs`) — all in files this change did not touch. Per CLAUDE.md ("a
warning or failure `main` already has was already broken"), it is named here rather than folded into
this branch's diff; fixing it is a change of its own. Every new connection string this change's own
new test files build (`InvoiceCalendarEventsMigrationTests.fs`, `InvoiceCalendarEventStoreTests.fs`,
`Contracts/InvoiceCalendarEventsPersistedShapeTests.fs`, and the new Contract/E2E files) carries
`;Pooling=False` and is not among the offenders.

One Integration-level run surfaced the documented LiteDB `BsonMapper` warm-up race
(CLAUDE-project.md → *Per-integration databases*) in
`ThunderbirdDatabaseContextModuleTests.getDatabaseContext exposes a working collection getter for
every one of the five entities` — a known, intermittent, not-this-change's flake. A same-filter
re-run passed 360/360 with no failure at all, consistent with the race rather than a regression.

`Contracts/DomainIsolationTests.fs` (3/3) and the `AssertDomainReferencesNothing` build target both
still pass — `MyDogsbody.Domain` has zero `ProjectReference` elements. `MainWindow.xaml.cs` is
unchanged since `main` (task 7.4's stated outcome: this is the last registration in the series and
the host has still only been touched once, in change #3).

### Series acceptance checks (task 11.6)

- **No `Credentials.db`, no `MyDogsbody.Integrations.Credentials`, no `MyDogsbody.Enums`, no
  `MyDogsbody.Domain/Credentials/`** — confirmed. (Stale, untracked `bin`/`obj` build-output
  directories for the deleted `MyDogsbody.Enums` and `MyDogsbody.Integrations.Credentials` projects
  remain on the local disk from before change #5 — not tracked by git, not referenced by
  `MyDogsbody.sln`, and not part of the solution; harmless residue, not a regression.)
- **`UI.Portal` references only `UI.Types`** (and transitively `Exceptions.Types`) — confirmed, one
  `ProjectReference` in `MyDogsbody.UI.Portal.fsproj`.
- **Scan windows exist as rows and nowhere else** — confirmed, no hardcoded day list anywhere under
  `MyDogsbody.UI.Portal/` or `MyDogsbody.UI.Types/`.
- **The domain still names no Thunderbird, Google, LiteDB, SQLite, MIME or PDF type** — confirmed, no
  `open` of an outer-ring namespace anywhere under `MyDogsbody.Domain/` (checked by grep for
  `ILiteCollection`, `SqliteConnection`, `LiteDatabase`, `HttpClient`, `Google.Apis`).
- **Still exactly two mapping points per feature** — `GoogleEntityMappers.fs`/store-side mappers at
  the bottom, `InvoiceSyncApiMappers.fs` at the top; this change added no third hop.
- **Syncing twice in a row makes no API calls the second time** — asserted directly by
  `SyncInvoicesToCalendarWorkflowTests.fs`'s idempotency test (Phase 4, task 4.3) and by
  `InvoiceSyncFlowTests.fs`'s "an unchanged state produces an empty plan and makes no calls" (E2E).
- **No `DeleteEvent` for an invoice merely outside the window, and no plan from a failed read** —
  the two headline guard tests from Phase 2, plus their E2E-level echo.

### Task 11.4 — manual verification against a real Google calendar (open; needs the user)

**Not performed.** This requires a real Google account, a real OAuth consent flow through a system
browser, and a real calendar the running WPF app can write to and delete from — none of which an
agent in this environment can do safely or at all. This is the one operation in the whole change
whose failure mode the automated suite cannot fully model (task 11.4's own words), and it is the
step most worth doing by hand before merging.

**What's needed, concretely**, run from a built `MyDogsbody.exe` (or `dotnet run --project
MyDogsbody\MyDogsbody.csproj` per CLAUDE-project.md → *Commands*) against a real Google account with
a disposable test calendar:

1. Register the account (or reuse one from change #6's own manual verification) and choose the
   default invoice calendar.
2. On the invoices page, with at least one uploadable invoice in the ledger: preview the sync plan,
   confirm it names the invoice, run it, and confirm the event appears on the real calendar with the
   right title and date.
3. Change something about that invoice that would change the event (or hand-edit the event's title
   in Google Calendar) and re-sync; confirm the row shows "changed" beforehand and the event is
   rewritten — including that a hand-edit gets overwritten (Q2.14), since the page's own notice
   (task 8.9) says this will happen.
4. Delete that invoice (or let it fall out of the ledger) and re-sync; confirm the plan shows the
   delete, that a confirmation dialog appears and lists it, and that the event is actually removed
   from the real calendar only after confirming.
5. Sync once more with nothing outstanding; confirm the plan is empty and the page says so, without
   showing an enabled button that would do nothing.

Record what was run and what was observed here (or in a follow-up commit to this file) before
merging, per task 11.4.

## Friction #19 — real due-date coverage (task 12.2)

**Not independently re-measured in this change** — the measurement the checkpoint asks for needs a
real Thunderbird profile with real invoice mail, which this environment does not have access to, and
the question was already asked and answered as far as it can be from `invoice-extraction`'s own
`outcome.md` (§12.5, 2026-08-29):

> `MeasureScan` in measurement mode, three "token" suppliers guessed from a 730-day discovery dump
> against the real profile: **0 invoices extracted, 0 with a due date — coverage undefined (0/0)**.
> 8 of 234,446+ messages matched a supplier at all; the templates were guesses (no human had yet
> authored real rules against a real PDF from this mailbox), so `RuleFoundNothing` ate all 8. The
> ceiling in this particular mailbox over its ~2-year local window is roughly 6 plausible invoices
> — nowhere near the ~558-PDF / ~30-supplier scale the 12%→39% prediction (background.md's §3.10
> Finding 3) was made from. **"Friction #19, answered: this mailbox cannot produce a meaningful
> due-date-coverage figure... Change #7's value has to be judged on the friction list and the
> `DateFromField` mechanism itself... not on a measured % from here."**

Nothing in this change alters that conclusion, and nothing changed the mailbox or the templates
between then and now. What this change *does* contribute to friction #19 is the mechanism itself,
finished and tested: `DateFromField` (built and unit-tested in `invoice-templates`) plus
`UploadableInvoice.ofStored` (Phase 1 of this change) make "no due date" a **compile-time** fact —
the sync workflow cannot be handed an invoice that lacks one — so the day someone authors real
templates against this mailbox's real suppliers, whatever due-date coverage they achieve is exactly
what reaches the calendar, with no further plumbing needed. Measuring the real percentage remains
open, gated on template authoring only the maintainer can do (the same conclusion §12.5 already
reached), not on anything this change left unfinished.

## What was built, phase by phase

All twelve phases of `docs/changes/invoice-calendar-sync/tasks.md` are checked off except task 11.4
(above). Test counts are cumulative per commit on `change/invoice-calendar-sync`:

| Phase | What | New tests |
| --- | --- | --- |
| 1 | Types: `InvoiceSyncKey`, `UploadableInvoice`, the events half of `CalendarTypes.fs`, `CalendarDateRangeWorkflow` | 40 |
| 2–3 | The two hazard guards and the pure `diff` | 14 |
| 4 | `SyncInvoicesToCalendarWorkflow.executePlan` | 10 |
| 5 | `GoogleCalendarClient`'s four event operations | 30 (file total; 15 new) |
| 6 | `InvoiceCalendarEvents` migration and store | 20 |
| 7 | Composition root (`InvoiceSyncApiFactory`/`Mappers`, the four `GoogleAccountApiFactory` event bindings) | — (covered by Phases 6/9's tests plus manual build verification) |
| 8 | UI: sync-status column, plan preview, delete confirmation, orphaned events, per-row outcomes, bulk button, app-owned notice, empty-plan messaging | ~20 |
| 9 | Contract suites for the seven new dependency types, `InvoiceSyncApi`, and the persisted shape | 58 |
| 10 | E2E flows over a real temp SQLite database and a real temp `Google.db`, HTTP stubbed | 7 |
| 11 | The gate (this file) | — |

## Known, recorded limitations (not defects)

- **The sync targets one Google account** — the first registered account with a default invoice
  calendar chosen. Syncing to more than one ready account at once is out of scope; design.md's own
  examples work in terms of "the account" throughout, and no domain type associates an invoice with
  a particular Google account (design decision, `InvoiceSyncApiFactory.fs`'s own doc comment).
- **`InvoiceSyncApiFactory.createInvoiceSyncApi` has no stubbing seam of its own** for a full
  real-API contract test — `createInvoiceSyncApiWith` (added in Phase 10) closes this for E2E
  purposes, but `Contracts/InvoiceSyncApiContractTests.fs`'s real/fake shared suite is still scoped
  to paths that never reach Google, matching the same accepted scoping
  `Contracts/GoogleAccountApiContractTests.fs` already uses. Recorded as a named follow-up, not a
  silent gap.
- **A duplicate event added by hand is not detected** (O.3) — an event with no extended property
  cannot be recognised as belonging to an invoice, by design (Q2.4); the page's app-owned notice
  (task 8.9) states the consequence rather than hiding it.
- **A hand-edited event's title or date is reverted on the next sync** (Q2.14) — accepted, and
  stated on the page.
- Optional tasks O.1 (async batching via FsToolkit.ErrorHandling), O.2 (reminders), O.4 (a dry-run
  mode) and O.5 (a per-invoice sync history view) were not built — none were required, and O.1's own
  condition ("revisit if the interface feels stuck") was not met: the blocking calls run off the
  render thread via `startWork` throughout, the same as change #6 left them.
