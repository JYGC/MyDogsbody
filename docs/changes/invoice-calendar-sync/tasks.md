# Tasks — Invoice calendar sync

Change **#7 of 7**. Depends on **#4 and #6**. [`requirements.md`](requirements.md) ·
[`design.md`](design.md) · [decision record](../invoice-to-calendar/background.md)

**Branch: `change/invoice-calendar-sync`, cut from `main` once #4 and #6 have merged.** Everything in
this file lands on it, and it merges **only** when Phase 11 has passed in full — zero build errors,
zero test failures, zero skips, all four levels. No other change shares this branch, and none of this
work happens on `main`. **This is the only change in the series that can destroy data outside the
application**, so its two guard tests deserve a reviewer's whole attention rather than a scroll past
other features.
See [background → *One branch per change*](../invoice-to-calendar/background.md#one-branch-per-change).

**The ordering rule, per task:** where a task produces production code, its unit test is written
first, run, and confirmed to fail *for the reason expected* before the implementation. Tasks marked
*(test-first)* carry production code.

**Reserved migration timestamp for this change: `20260809000010`.**

> ### Write the two guard tests first — before the diff exists
>
> Phase 2 puts the two hazard tests in **before** `DiffInvoicesAgainstCalendarWorkflow` is written,
> against the signature alone. They will not compile, then they will fail, then they will pass. That
> ordering is deliberate: these are the two defects that can delete a calendar the application does
> not own, and they are the reason the diff takes a `LedgerSnapshot` rather than a list.

**No test may make a real Google call.** Every calendar operation is behind a dependency function
type; every test binds a lambda or a stubbed HTTP handler.

---

## Phase 1 — Types (required)

- [ ] **1.1** *(test-first)* `InvoiceSyncKey` in `Domain/Calendar/InvoiceSyncKey.fs` — **the one
      derivation** (Q2.10).
      Tests: `derive` is stable for the same supplier and reference; two different invoices give
      different keys; `parse (value (derive a b))` round-trips; an unparseable value returns an
      error; the extended-property name is a single literal.
      *Rationale:* this key identifies a row in the ledger's unique index, an event on a calendar,
      **and** a tombstone. Three hand-rolled derivations would agree right up until one did not.
- [ ] **1.2** *(test-first)* `UploadableInvoice` and `UploadableInvoice.ofStored` in
      `Domain/Invoices/InvoicesTypes.fs`.
      Tests: a stored invoice with a due date converts with every field asserted; **one without a due
      date returns `None`**.
      *Outcome:* `DueDate` is **not** an option on this type, so the sync workflow cannot be handed
      an invoice that has none — a compile-time fact rather than a runtime check (Q1.10).
- [ ] **1.3** `AllDayEvent`, `CalendarEventId`, `CalendarEvent`, `CalendarDateRange`, `SyncAction`,
      `LedgerSnapshot`, `SyncOutcome`, `SyncedInvoice`, the new `CalendarError` cases
      (`EventRejected`, `EventNoLongerExists`), and the six new dependency function types.
      *Note:* `AllDayEvent` carries **no time, no time zone, no duration** — every invoice event is
      all-day on the due date, so the domain carries nothing it never sets and the mapper cannot
      invent one.
- [ ] **1.4** *(test-first)* `CalendarDateRangeWorkflow`.
      Tests, with a fixed clock: the range is **mirrored** around today, not applied backwards only;
      **it stretches forward when an invoice in view falls due beyond it** — a supplier on 60-day
      terms inside a 14-day window; it never produces a range that excludes an invoice in view; an
      empty invoice list still gives a valid mirrored range.

## Phase 2 — The two guards (required) — write these before the diff

- [ ] **2.1** *(test-first)* **`diff produces no delete for an invoice merely outside the window`.**
      180 days of invoices in `AllLedgerKeys`, one in `InWindow`, events present for all of them.
      Assert **zero** delete actions.
      *This is the single most important test in the change. Get it wrong and narrowing the picker
      from 180 days to 7 deletes six months of calendar entries* (friction #18a).
- [ ] **2.2** *(test-first)* **`a failed calendar read produces no plan`.**
      Bind `listCalendarEvents` to a lambda returning `Error`. Assert the result is `Error` — **not
      an empty plan, not a plan of deletes** (friction #18b).
- [ ] **2.3** A check that **`Result.defaultValue` appears nowhere** in the sync workflow files.
      *Hazard (b) has exactly one way in, and this closes it at one line.*

## Phase 3 — The diff (required)

- [ ] **3.1** *(test-first)* `DiffInvoicesAgainstCalendarWorkflow.diff` — **pure**, table-driven over
      `(ledger, events) → expected plan`, every action's payload asserted.
      Tests: an invoice with no matching event → `CreateEvent`; a matching event with a different
      **title** → `UpdateEvent`; a different **date** → `UpdateEvent`; both agreeing →
      `LeaveAlone`; an event whose key is **absent from `AllLedgerKeys`** → `DeleteEvent` carrying
      the event id **and the sync key**; a due date change produces an **update**, not a delete and a
      create; and 2.1 and 2.2 both pass.
      *Outcome:* the workflow has **no dependency parameters** — no network, no clock, no store.
      *Depends on:* 2.1, 2.2, 1.3.
- [ ] **3.2** *(test-first)* Orphans and duplicates.
      Tests: an event with **no** sync key is reported as an orphan and is **never** a deletion
      candidate; an event with an **unparseable** key likewise; two events for one invoice →
      the first is updated and the second is **reported as a duplicate, not deleted**.
      *Rationale:* the app deletes only what it can prove it created; reporting is recoverable,
      deleting is not.
- [ ] **3.3** *(test-first)* `buildPlan` — the pipeline that binds the read before the pure diff.
      Tests: `NoDefaultCalendar` when the account has none, **with `listCalendarEvents` never
      called**; the range handed to the read is the one 1.4 derives; a failed read short-circuits
      (2.2 again, now through the real pipeline).
      *Depends on:* 3.1, 1.4.

## Phase 4 — Executing the plan (required)

- [ ] **4.1** *(test-first)* `SyncInvoicesToCalendarWorkflow`.
      Tests: `LeaveAlone` makes **no API call at all** — a recording fake asserts zero;
      a create stamps the extended property and calls `markSynced`;
      an update **rewrites title and date unconditionally** (Q2.14);
      a delete calls `clearSyncRecord`.
- [ ] **4.2** *(test-first)* Partial failure and already-gone.
      Tests: a failure mid-batch **continues** and reports per action (Q2.8); the earlier successes
      stay recorded; **`EventNoLongerExists` on an update or delete is `AlreadyGone`, a success, not
      a failure** — the calendar already agrees with the target state; `CalendarNoLongerExists`
      **stops** the batch; `NotAuthorised` mid-batch stops the batch and leaves the sync records
      consistent with what actually happened.
- [ ] **4.3** *(test-first)* **Idempotency — the bar Q2.6 raised.**
      Execute a plan, re-derive one over the resulting state, and assert **both** that every action
      is `LeaveAlone` **and** that a recording fake saw **zero calls** on the second pass.
      *Not "creates no duplicates" — that was the insert-only bar. With updates in play, a second run
      must make no API calls, or every run quietly rewrites every event.*

## Phase 5 — Google adapter (required)

- [ ] **5.1** *(test-first)* `GoogleCalendarClient.listEvents` — querying by
      `privateExtendedProperty`, bounded by the date range.
      Tests, against a **stubbed `HttpMessageHandler`**: events with the property present; events
      **without** it (an event added by hand — it must come back with `SyncKey = None`); a **paged**
      list; `403` → `NotAuthorised`; `429` → `CalendarRateLimited`; `410` → `CalendarNoLongerExists`.
- [ ] **5.2** *(test-first)* `createEvent` — an **all-day** event on the due date, with the title and
      description specified, **no reminder** (Q2.2), and the extended property stamped.
      Tests: the request body carries an all-day `start.date` (not `start.dateTime`); the property is
      present with the derived key; a rejection maps to `EventRejected`.
- [ ] **5.3** *(test-first)* `updateEvent` and `deleteEvent`.
      Tests: **`404` maps to `EventNoLongerExists`**, which 4.2 relies on being a success rather than
      a failure; `403` maps to `NotAuthorised`.
- [ ] **5.4** `ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listEvents /
      createEvent / updateEvent / deleteEvent`.

## Phase 6 — Persistence (required)

- [ ] **6.1** *(test-first)* `Migration_20260809000010_CreateInvoiceCalendarEventsTable.fs`.
      Tests: columns; the unique index on `InvoiceId`; **deleting an invoice cascades to its sync
      record**; `Down()` reverses it.
- [ ] **6.2** *(test-first)* `InvoiceCalendarEventStore.fs` — `markSynced`, `clearSyncRecord`,
      `loadSyncRecords`, and `loadAllLedgerKeys`.
      Tests *(Integration)*: round trips; `markSynced` twice updates rather than duplicating;
      **`loadAllLedgerKeys` returns every key in the ledger, ignoring any window** — this is the
      query hazard (a)'s guard depends on.
      Tests *(Unit)*: error paths assert the declared `ActionNames` string, message and inner
      exception.
- [ ] **6.3** `ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.*`.

## Phase 7 — Composition root (required)

- [ ] **7.1** *(test-first)* `InvoiceSyncApiMappers.fs` — domain ⇄ UI, error translation.
      Tests: a `SyncAction` maps to a UI plan row **naming the invoice**, not just an event id;
      each `SyncOutcome` maps to its per-row rendering; each new `CalendarError` case maps to its
      intended action and message with the expected/unexpected split asserted.
- [ ] **7.2** *(test-first)* `InvoiceSyncApiFactory.createInvoiceSyncApi` with `GetSyncPlan` and
      `ExecuteSyncPlan`.
      Tests *(Integration)*: both members against a real temp SQLite database and stubbed HTTP.
      No module-level I/O.
- [ ] **7.3** `ActionNames.MyDogsbody.Startup.InvoiceSyncApi.*`.
- [ ] **7.4** `Startup.fs`: bind the four event operations, register `invoiceSyncApi`.
      *Outcome:* `MainWindow.xaml.cs` unchanged. **This is the last registration in the series and
      the host has still only been touched once, in change #3.**

## Phase 8 — UI (required)

- [ ] **8.1** `MyDogsbody.UI.Types`: `InvoiceSyncApi`, `SyncPlanRowUiType`, `SyncOutcomeUiType`,
      a sync-status field on `InvoiceUiType`, and on `InvoicesModule`:
      `SelectedInvoiceIdsAval`, `ToggleInvoice`, `ClearSelection`, `PendingActionCountAval`.
- [ ] **8.2** *(test-first)* Module-creator additions.
      Tests: ticking rows limits the plan to them; **no ticks means everything outstanding in view**;
      **a rescan clears the selection** — a tick against a row that no longer exists is worse than no
      tick at all; selection is view state and is **not persisted**.
- [ ] **8.3** The sync-status column: **three per-row states** — up to date, missing, changed
      (design decision 5). An invoice with no due date shows *not uploadable* with its reason and
      **no** sync status.
- [ ] **8.4** The plan preview. Every action shown, **naming the invoice**, before anything runs
      (Q2.13).
- [ ] **8.5** **Confirmation required for any plan containing a delete**, listing what will be
      deleted. *This is the guard that makes delete permission trustworthy.*
- [ ] **8.6** The orphaned-events view — events whose invoice has left the ledger, and events with no
      or unparseable sync keys, the latter marked as needing attention rather than deletion.
- [ ] **8.7** Per-row outcomes after a partial failure (Q2.8).
- [ ] **8.8** The bulk sync button, stating the count it will act on; **disabled with its reason**
      when the account is not ready (Q2.11) or no account is registered.
- [ ] **8.9** **A sentence on the page saying that an invoice event is app-owned and that a sync will
      overwrite a hand-edited title or date** (Q2.14). The behaviour is defensible; hiding it is not.
- [ ] **8.10** An empty plan says so plainly rather than showing an enabled button that does nothing.

## Phase 9 — Contract suites (required)

- [ ] **9.1** Shared suites for `ListCalendarEvents`, `CreateCalendarEvent`, `UpdateCalendarEvent`,
      `DeleteCalendarEvent`, `MarkSynced`, `ClearSyncRecord`, `LoadAllLedgerKeys` — every fake **and**
      the real adapter over stubbed HTTP (friction #2). `MemberData` sources are **public** `let`s.
- [ ] **9.2** `InvoiceSyncApi` contract suite: real record and every fake.
- [ ] **9.3** Persisted-shape test for `InvoiceCalendarEvents`.

## Phase 10 — End to end (required)

- [ ] **10.1** `E2E/InvoiceSyncFlowTests.fs` against a real temp SQLite file and stubbed HTTP:
      a create appears in the plan, executes, and the row shows up to date;
      an update shows the row as changed beforehand;
      **a delete requires confirmation before executing**;
      an unchanged state produces an empty plan and **no calls**;
      a partial failure reports per row;
      a not-ready account disables the button with its reason;
      ticking rows limits the plan and a rescan clears the selection.
- [ ] **10.2** Confirm no test makes a real Google call or reaches `Startup.Startup`.

## Phase 11 — Gate (required)

- [ ] **11.1** `dotnet build MyDogsbody.sln` — zero errors.
- [ ] **11.2** `dotnet test` — zero failures, **zero skips**, all four levels. Record totals per level.
- [ ] **11.3** `Contracts/DomainIsolationTests.fs` and `AssertDomainReferencesNothing` still pass.
- [ ] **11.4** **Manual verification against a real Google calendar — and it must include a delete.**
      Create, update, delete, then a second sync making **no calls**. The delete is the one operation
      whose failure mode the suite cannot fully model. Record what was run and what was observed.
- [ ] **11.5** Confirm `MainWindow.xaml.cs` is untouched.
- [ ] **11.6** **Series acceptance checks** — the end-state list in the decision record:
      still exactly two mapping points per feature;
      `UI.Portal` references only `UI.Types` and `Exceptions.Types`;
      no `Credentials.db`, no `MyDogsbody.Enums`, no `MyDogsbody.Domain/Credentials/`;
      scan windows exist as rows and nowhere else;
      the domain still names no Thunderbird, Google, LiteDB, SQLite, MIME or PDF type.

## Phase 12 — Documentation (required)

- [ ] **12.1** `CLAUDE-project.md`: the new migration, the new workflows, the *Build state* totals,
      and a note that the invoices page now writes to an external calendar.
- [ ] **12.2** `outcome.md`: totals per level; the manual verification from 11.4 **including the
      delete**; the series acceptance checks from 11.6; and **the real due-date coverage achieved**,
      against the 12% → 39% the measurement predicted (friction #19) — this is the number that says
      whether the calendar this change builds is actually full.
- [ ] **12.3** Open `change/invoice-calendar-sync` for review, with this file's checkboxes ticked and
      `outcome.md` on the branch. **Merge only after Phase 11 passed in full.**
      *Point the reviewer at Phase 2 before anything else. Tasks 2.1, 2.2 and 2.3 are the whole
      reason this branch is reviewable on its own: they are what stop a defect in a pure function
      from deleting a calendar the application neither owns nor can restore.*

---

## Optional

- [ ] **O.1** Take **FsToolkit.ErrorHandling** for `asyncResult` instead of blocking (friction #1).
      Change #6 named this change's batch as the condition for revisiting. Decide it from how the
      interface actually feels with a real batch.
- [ ] **O.2** Reminders on events. Q2.2 chose none.
- [ ] **O.3** Recognise an event added by hand for the same invoice. It carries no extended property,
      so this would need a heuristic on title or date — which is exactly what the extended property
      was chosen to avoid. Recorded as a known limitation, not a task.
- [ ] **O.4** A dry-run mode that shows the plan and never offers to execute it. The plan preview
      plus the delete confirmation already give most of this.
- [ ] **O.5** Show a sync history per invoice, from `InvoiceCalendarEvents`. The data is already
      there and it answers *"when did we last touch this, and on whose calendar?"*.

## Known risks carried into this change

- **Friction #18 (a) — deleting on window absence.** The worst thing this feature could do.
  `LedgerSnapshot` makes it a type-level distinction; task 2.1 asserts it directly.
- **Friction #18 (b) — deleting on a failed read.** The read binds before the diff; tasks 2.2 and 2.3
  are the guards.
- **Friction #19 — the calendar may stay mostly empty.** Only ~12% of measured invoices state a due
  date; `DateFromField` takes it to ~39%. Task 12.2 records what was actually achieved.
- **Friction #2 — contract tests against a network service.** Stub-backed suites plus recorded manual
  verification, **including a real delete**.
- **Friction #1 — blocking on async calls in a batch.** This is where a batch first exists; O.1 is
  the decision point.
- **A second sync quietly rewriting every event.** Task 4.3 asserts an all-`LeaveAlone` plan *and*
  zero calls.
- **An event added by hand gets duplicated.** Accepted, stated on the page, recorded as O.3.
- **A hand-edited event gets reverted** (Q2.14). Accepted, and task 8.9 makes the page say so.
