# Requirements — Invoice calendar sync

Change **#7 of 7**. Depends on **#4 and #6**. See
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md) for the decision
record; question ids (`Q2.1`–`Q2.14`) and friction numbers (#1, #2, #18, #19) resolve there.

**What this change is for.** The diff and the sync. Asks #1 and #3.

> ### This is the only change in the series that can destroy data outside the application.
>
> With `DeleteCalendarEvent` bound, a defect in a **pure function** can remove entries from a
> calendar the app neither owns nor can restore. Two hazards, both cheap to guard and both
> unrecoverable if missed (friction #18):
>
> **(a)** deletion driven by *window* absence rather than *ledger* absence — narrowing the picker
> from 180 days to 7 must never read as "173 days of invoices disappeared";
>
> **(b)** deletion driven by a *failed read* — if the event listing comes back empty because a token
> expired, an unguarded diff concludes every event is missing and its mirror image concludes every
> invoice is orphaned.
>
> **The tests for those two are the headline tests of this change**, and they are both against the
> pure diff, where they cost nothing.

---

## The idempotency key

WHEN an event is created THE SYSTEM SHALL stamp it with a private extended property carrying the invoice's **supplier and invoice reference** (Q2.4, Q2.10), under one well-known property name.
WHEN an event is created THE SYSTEM SHALL also carry the local invoice identifier **for diagnostics only**, and SHALL NOT use it for matching — rebuilding the ledger from scratch must not make every event read as missing.
WHEN events are read back THE SYSTEM SHALL query them by that private extended property.
WHEN the key is derived THE SYSTEM SHALL derive it in **one function**, used everywhere — it identifies a row in the ledger's unique index, an event on a calendar, and a tombstone, and three hand-rolled derivations would agree right up until one of them did not.
WHEN an event was not created by this application THE SYSTEM SHALL NOT recognise it, and the page SHALL NOT claim to detect "any event for this invoice" — an event added by hand carries no property, so the diff will not see it and a sync will create a second one. That is the accepted cost of robustness against renaming.

---

## The date range

WHEN events are listed THE SYSTEM SHALL bound the query by a date range derived from the same scan window the invoices page uses — one knob, not two (Q2.5).
WHEN the range is derived THE SYSTEM SHALL mirror the window around today rather than applying it backwards only, because the scan window looks **backwards at when mail arrived** while an event sits on its **due date**, which is normally ahead of that.
WHEN any invoice in view falls due later than the mirrored range THE SYSTEM SHALL stretch the range forward to cover it — a supplier on 60-day terms inside a 14-day window is not exotic.
WHEN the range is applied THE SYSTEM SHALL never produce a range that excludes an invoice currently in view, because an event read as absent is an event that gets created twice.

---

## The plan

### Four actions

WHEN the diff runs THE SYSTEM SHALL produce a **plan** of actions, not a status list: create, update, delete, or leave alone (Q2.6).
WHEN an invoice in view has no matching event THE SYSTEM SHALL produce a **create**.
WHEN an event's title or date disagrees with the ledger THE SYSTEM SHALL produce an **update**.
WHEN an event and the ledger agree THE SYSTEM SHALL produce a **leave alone**, and SHALL make no API call for it.
WHEN an event's invoice has **left the ledger** THE SYSTEM SHALL produce a **delete**.
WHEN the diff runs THE SYSTEM SHALL be a pure function — no network, no clock, no store.

### The two guards

WHEN an invoice is merely **outside the current window** THE SYSTEM SHALL NOT produce a delete for its event. Narrowing hides; it does not forget (Q2.9).
WHEN the diff is given what it needs to tell those apart THE SYSTEM SHALL be given the whole ledger's key set, not only the windowed invoices, so the distinction is structural rather than a condition someone remembered to write.
WHEN reading the calendar fails THE SYSTEM SHALL abort and produce **no plan at all**, and SHALL NOT treat a failed read as an empty calendar.
WHEN a plan contains any delete THE SYSTEM SHALL require explicit confirmation before executing it (Q2.13).
WHEN a plan is produced THE SYSTEM SHALL show it before it runs — a plan you can see before it runs is the difference between trusting this feature with delete permission and not.

### What an event looks like

WHEN an event is created THE SYSTEM SHALL create an **all-day event on the due date** (Q2.1).
WHEN an event is created THE SYSTEM SHALL set its title and description as specified, and SHALL set **no reminder** (Q2.2).
WHEN an event is modelled in the domain THE SYSTEM SHALL carry only a date, a title and a description — no time, no time zone, no duration — so a mapper cannot invent one.
WHEN an invoice has **no due date** THE SYSTEM SHALL be unable to hand it to the sync at all: the workflow accepts only a type that carries a due date, so "an invoice with no due date cannot become an event" is a compile-time fact rather than a runtime check (Q1.10).

### Overwriting hand edits

WHEN an update runs THE SYSTEM SHALL rewrite the event's title and date **unconditionally** — the event is app-owned and always wins (Q2.14).
WHEN the diff sees a disagreement THE SYSTEM SHALL NOT try to tell "the invoice changed" from "somebody edited the event by hand"; both are the same case.
WHEN this behaviour exists THE SYSTEM SHALL say so on screen. It is the honest cost of Q2.6: the extended property was chosen so a rename would not cause a duplicate, and it now also means a rename gets **reverted**. Justified because a title disagreeing with the ledger is worse than a lost edit — but the page must not pretend otherwise.

---

## Executing the plan

WHEN the plan runs THE SYSTEM SHALL make no API call for a leave-alone action.
WHEN an action fails THE SYSTEM SHALL continue with the remaining actions and report the outcome **per row** (Q2.8), rather than stopping at the first failure.
WHEN an update or delete targets an event somebody has already removed by hand THE SYSTEM SHALL treat it as a **success**, not a failure — the calendar already agrees with where we were trying to get to.
WHEN a create succeeds THE SYSTEM SHALL record the sync against the invoice.
WHEN a delete succeeds THE SYSTEM SHALL clear the sync record for that invoice.
WHEN an update succeeds THE SYSTEM SHALL refresh the sync record's timestamp.
WHEN the same plan is executed and then re-derived over unchanged data THE SYSTEM SHALL produce a plan of nothing but leave-alones. **Not "creates no duplicates" — that was the insert-only bar.** With updates in play, a second run must make no API calls at all, or every run quietly rewrites every event.

---

## Which calendar

WHEN a sync runs THE SYSTEM SHALL use the **default invoice calendar of the chosen Google account** (Q2.3).
WHEN the chosen account has no default calendar THE SYSTEM SHALL show it as *not ready* and disable the sync action, with the reason stated (Q2.11).
WHEN a sync runs THE SYSTEM SHALL offer no per-upload calendar override (Q2.12).
WHEN no Google account is registered THE SYSTEM SHALL say so and disable the sync action.

---

## Selection

WHEN the invoice table is displayed THE SYSTEM SHALL offer a checkbox per row (Q2.7).
WHEN rows are ticked and the sync button is pressed THE SYSTEM SHALL act on the ticked rows only.
WHEN no rows are ticked and the sync button is pressed THE SYSTEM SHALL act on everything outstanding in view.
WHEN the sync button is displayed THE SYSTEM SHALL state how many actions it will take.
WHEN a rescan happens THE SYSTEM SHALL clear the selection — a tick against a row that no longer exists is worse than no tick at all.
WHEN selection state is held THE SYSTEM SHALL hold it as view state and SHALL NOT persist it.

---

## Sync status

WHEN an invoice row is displayed THE SYSTEM SHALL show its sync status: up to date, missing, or changed.
WHEN an event exists whose invoice has left the ledger THE SYSTEM SHALL show it as **orphaned in the plan and in an orphaned-events view**, because there is no invoice row to attach it to.
WHEN an invoice has no due date THE SYSTEM SHALL show it as not uploadable with the reason, and SHALL NOT give it a sync status.
WHEN the sync record table is consulted THE SYSTEM SHALL treat it as **history and diagnostics**, never as the answer to "is it there?" — the calendar is the source of truth for the diff, and if the two disagree the calendar is right and the table is out of date.

---

## Persistence

WHEN the migration runner is applied THE SYSTEM SHALL create an `InvoiceCalendarEvents` table recording, per sync: the invoice, the Google account, the calendar, the event identifier and when it was last synced.
WHEN an invoice's event is deleted THE SYSTEM SHALL clear its row.
WHEN this table is used THE SYSTEM SHALL answer only *"when did we last touch this event, and on whose calendar?"* — the first question worth asking when a sync did something unexpected.
WHEN the migration's `Down()` is run THE SYSTEM SHALL remove exactly what its `Up()` created.

---

## Testing

### The two tests this change exists to pass

WHEN an invoice is present in the ledger but outside the current window THE SYSTEM SHALL produce **no delete** for its event. **This is the single most important test in the change** — get it wrong and narrowing the picker from 180 days to 7 deletes six months of calendar entries.
WHEN the calendar read fails THE SYSTEM SHALL produce **no plan at all** — not an empty plan, not a plan of deletes.
WHEN those two are tested THE SYSTEM SHALL test them against the **pure** diff, where they cost nothing to run and nothing to set up.

### Levels

WHEN a domain function is added THE SYSTEM SHALL have a unit test written **before** the implementation, asserting every field of the success output and the exact error case with its payload.
WHEN the diff is tested THE SYSTEM SHALL use table-driven cases over `(ledger, events) → expected plan`, with every action's payload asserted.
WHEN idempotency is tested THE SYSTEM SHALL execute a plan and then re-derive one over the resulting state, asserting **every** action is a leave-alone and **no** API call was made.
WHEN partial failure is tested THE SYSTEM SHALL assert that a failure in the middle of a batch leaves the earlier successes recorded and the later actions attempted.
WHEN an event already removed by hand is targeted THE SYSTEM SHALL be asserted to report success and not to fail the batch.
WHEN the Google event operations are tested THE SYSTEM SHALL run their contract suites against every fake and against the real adapter over a stubbed HTTP handler, with live verification recorded as **manual coverage in the change description**.
WHEN the sync flow is complete THE SYSTEM SHALL have an E2E test covering a create, an update, a delete requiring confirmation, a leave-alone making no call, a partial failure reported per row, and a not-ready account disabling the button.
WHEN a test is added THE SYSTEM SHALL tag it with its level.

### Gate

WHEN this change is complete THE SYSTEM SHALL build the whole solution with zero errors and pass the whole suite with zero failures and zero skips.
WHEN this change is complete THE SYSTEM SHALL have been verified by hand against a real Google calendar, including a delete, and the result recorded.

---

## Edge cases

WHEN two invoices share a supplier and reference THE SYSTEM SHALL be unable to reach this change at all, because the ledger's unique index refuses the second.
WHEN an event carries the extended property but its value is unparseable THE SYSTEM SHALL report it as an orphaned event needing attention, and SHALL NOT delete it.
WHEN the same invoice has two events on the calendar THE SYSTEM SHALL update the first and report the second as a duplicate, and SHALL NOT delete it without confirmation.
WHEN a create fails because the calendar no longer exists THE SYSTEM SHALL stop the batch and report it, because every remaining action would fail the same way.
WHEN a rate limit is returned mid-batch THE SYSTEM SHALL report it distinctly from a permission failure and record which actions completed.
WHEN the token expires mid-batch THE SYSTEM SHALL stop, report it, and leave the sync records consistent with what actually happened.
WHEN a due date changes so an event must move THE SYSTEM SHALL produce an update, not a delete and a create.
WHEN an invoice is deleted by hand and the next sync runs THE SYSTEM SHALL produce a delete for its event, visible in the plan before it runs (Q2.13).
WHEN an invoice is tombstoned and un-deleted between syncs THE SYSTEM SHALL produce a create on the next sync.
WHEN the plan is empty THE SYSTEM SHALL say so plainly rather than showing an enabled button that does nothing.

---

## Out of scope

- **Reading events this application did not create.** They carry no extended property; the page must not claim to detect them.
- **A per-upload calendar override** (Q2.12).
- **Reminders on events** (Q2.2).
- **Syncing anything other than invoices** to a calendar.
- **Two-way sync.** The ledger is the source of truth; the calendar is written to.
- **Scheduled or background syncing.** A sync is something the user asks for and confirms.
