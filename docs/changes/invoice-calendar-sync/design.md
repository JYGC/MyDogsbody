# Design — Invoice calendar sync

Change **#7 of 7**. Depends on **#4 and #6**. Requirements in [`requirements.md`](requirements.md);
decision record in [`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md).

---

## The one thing this design is really about

Every other change in the series can, at worst, write a wrong row to a database it owns. This one can
**delete entries from a calendar the application neither owns nor can restore** — and it can do so
from a defect in a *pure function*, which is normally the safest kind of code in the codebase.

So the central design question is not "how do we compute a diff". It is: **how do we make the two
known hazards structurally impossible rather than conditionally avoided?**

| Hazard | Naive shape that causes it | The structural answer |
| --- | --- | --- |
| **(a)** Deleting because an invoice is outside the *window* rather than gone from the *ledger* | `diff : StoredInvoice list -> CalendarEvent list -> SyncAction list`, where the list is the windowed one | The diff takes a `LedgerSnapshot` carrying **both** the windowed invoices and **every key in the ledger**. It cannot be handed a windowed-only list, because that is not the type |
| **(b)** Deleting because a *read failed* and came back empty | `listCalendarEvents … \|> Result.defaultValue []` anywhere | The read is bound in the `result` pipeline **before** the pure diff is called, so an `Error` short-circuits and the diff is never reached. No defaulting, anywhere, and a test asserts a failed read yields `Error` rather than a plan |

Both are one-line ideas. Both are unrecoverable if missed. Everything below is arranged around them.

---

## System architecture and components

```
 UI.Portal  /invoices  (extended — the page change #4 built)
   InvoicesComponents.fs   + sync status column, per-row checkbox, bulk button,
                             plan preview, delete confirmation, per-row outcomes,
                             orphaned-events view
        ▼
 UI.Types   InvoiceSyncApi { GetSyncPlan; ExecuteSyncPlan }
            InvoicesModule + SelectedInvoiceIdsAval, ToggleInvoice, ClearSelection,
                             PendingActionCountAval
        ▼
 Startup    InvoiceSyncApiFactory.fs · InvoiceSyncApiMappers.fs
        ▼
 Domain     Calendar/CalendarTypes.fs   ← EXTENDED (events + the sync plan)
            Calendar/InvoiceSyncKey.fs           the ONE derivation
            Calendar/CalendarDateRangeWorkflow.fs
            Calendar/DiffInvoicesAgainstCalendarWorkflow.fs    ← PURE. the heart.
            Calendar/SyncInvoicesToCalendarWorkflow.fs         ← executes a plan
            Invoices/InvoicesTypes.fs   ← EXTENDED (UploadableInvoice, SyncedInvoice)
        ▲
 Integrations.Google
   GoogleCalendarClient.fs  + ListCalendarEvents, CreateCalendarEvent,
                              UpdateCalendarEvent, DeleteCalendarEvent
        ▲
 Database   InvoiceCalendarEventStore.fs  markSynced · clearSyncRecord · loadSyncRecords
 Migrations 20260809000010_CreateInvoiceCalendarEventsTable
```

**Reserved migration timestamp for this change: `20260809000010`.**

---

## Data models and interfaces

### `Calendar/CalendarTypes.fs` — the events half

```fsharp
/// The idempotency key. Supplier + invoice reference - the natural key from the ledger, NOT the
/// database id, so rebuilding the ledger from scratch does not make every event read as missing.
///
/// It identifies THREE things: a row in the ledger's unique index, an event on a calendar, and a
/// tombstone. ONE function derives it and everything else calls that function; three hand-rolled
/// derivations would agree right up until one of them did not.
type InvoiceSyncKey = private InvoiceSyncKey of string

module InvoiceSyncKey =
    [<Literal>]
    let PropertyName = "mydogsbody.invoice"        // the private extended property's name
    let derive (supplierId: SupplierId) (reference: InvoiceReference) : InvoiceSyncKey = …
    let parse  (value: string) : Result<InvoiceSyncKey, string> = …
    let value  (InvoiceSyncKey k) = k

/// Start date, title and description. No time, no time zone, no duration - Q2.1 makes every
/// invoice event all-day on the due date, so the domain carries nothing it never sets, and the
/// mapper cannot accidentally invent one.
type AllDayEvent = { Date: System.DateTime; Title: string; Description: string }

type CalendarEventId = private CalendarEventId of string

/// An event as read back. SyncKey is None when the extended property is absent or unparseable -
/// which is an orphan needing attention, never a deletion candidate.
type CalendarEvent =
    { Id: CalendarEventId; Event: AllDayEvent; SyncKey: InvoiceSyncKey option }

/// The bound Events.list needs.
///
/// Q2.5 ties it to the scan window, which needs saying carefully because the obvious reading is
/// wrong: the scan window looks BACKWARDS at when mail arrived, while an invoice event sits on
/// its DUE date, which is normally ahead of that. Querying [today - N, today] would miss the
/// event for every invoice not yet due, read it as absent, and create a second one.
type CalendarDateRange = private CalendarDateRange of System.DateTime * System.DateTime

/// What Q2.6 turns the diff into.
type SyncAction =
    | CreateEvent of UploadableInvoice
    | UpdateEvent of CalendarEventId * UploadableInvoice   // event disagrees with the ledger
    | DeleteEvent of CalendarEventId * InvoiceSyncKey      // the invoice is GONE FROM THE LEDGER
    | LeaveAlone  of CalendarEventId                       // identical - no API call at all

/// The input to the diff, and hazard (a)'s structural guard.
///
/// AllLedgerKeys is EVERY key in the ledger, windowed or not. InWindow is what the page is
/// showing. A DeleteEvent is produced only for an event whose key is absent from AllLedgerKeys -
/// so "outside the window" and "gone from the ledger" cannot be confused, because the diff is
/// never handed a windowed-only list in the first place.
type LedgerSnapshot =
    { InWindow: UploadableInvoice list
      AllLedgerKeys: Set<InvoiceSyncKey> }

type SyncOutcome =
    | Created  of InvoiceId * CalendarEventId
    | Updated  of InvoiceId * CalendarEventId
    | Deleted  of CalendarEventId
    | Skipped  of CalendarEventId                   // LeaveAlone - no call made
    | AlreadyGone of CalendarEventId                // update/delete on an event already removed
    | Failed   of SyncAction * CalendarError

// CalendarError gains:
//   | EventRejected        of reason: string
//   | EventNoLongerExists  of CalendarEventId      // expected, must NOT fail the batch

type ListCalendarEvents  = GoogleAccountId -> CalendarId -> CalendarDateRange
                                -> Result<CalendarEvent list, CalendarError>
type CreateCalendarEvent = GoogleAccountId -> CalendarId -> InvoiceSyncKey -> AllDayEvent
                                -> Result<CalendarEventId, CalendarError>
type UpdateCalendarEvent = GoogleAccountId -> CalendarId -> CalendarEventId -> AllDayEvent
                                -> Result<unit, CalendarError>
type DeleteCalendarEvent = GoogleAccountId -> CalendarId -> CalendarEventId
                                -> Result<unit, CalendarError>
```

### `Invoices/InvoicesTypes.fs` — two stage types

```fsharp
/// An invoice that can actually become an event. DueDate is NOT optional.
///
/// This is how "an invoice with no due date can't go on a calendar" stops being a runtime check:
/// the sync workflow accepts only this type, so an invoice missing a due date cannot reach it.
/// The invoice is still stored and still listed - it just isn't uploadable, and the table says why.
type UploadableInvoice =
    { Id: InvoiceId
      SupplierId: SupplierId
      Reference: InvoiceReference
      Amount: Money
      DueDate: DueDate }               // not an option

module UploadableInvoice =
    /// The only door. A StoredInvoice with no due date returns None, and the page renders that
    /// as "not uploadable" with the reason.
    let ofStored (invoice: StoredInvoice) : UploadableInvoice option = …

type SyncedInvoice = { Invoice: UploadableInvoice; EventId: CalendarEventId }

type MarkSynced      = InvoiceId -> GoogleAccountId -> CalendarId -> CalendarEventId
                           -> Result<unit, InvoiceError>
type ClearSyncRecord = InvoiceId -> Result<unit, InvoiceError>
```

### Workflows

| File | Signature | Purity |
| --- | --- | --- |
| `CalendarDateRangeWorkflow.fs` | `GetCurrentTime -> ScanWindowDays -> UploadableInvoice list -> CalendarDateRange` | Pure given the clock |
| `DiffInvoicesAgainstCalendarWorkflow.fs` | `LedgerSnapshot -> CalendarEvent list -> SyncAction list` | **Pure** |
| `SyncInvoicesToCalendarWorkflow.fs` | dependencies… → `SyncAction list -> Result<SyncOutcome list, CalendarError>` | Executes |

```fsharp
// The plan-building pipeline. Hazard (b) lives or dies on this shape.
let buildPlan
    (getCurrentTime: GetCurrentTime)
    (listCalendarEvents: ListCalendarEvents)
    (loadInvoices: LoadInvoices)
    (loadAllLedgerKeys: LoadAllLedgerKeys)
    (account: RegisteredGoogleAccount)
    (window: ScanWindowDays)
    : Result<SyncAction list, CalendarError> =
    result {
        let! calendarId =
            account.DefaultInvoiceCalendar
            |> Option.toResultOr (NoDefaultCalendar account.Id)

        let! inWindow = loadInvoices (Some cutoff) |> Result.map (List.choose UploadableInvoice.ofStored)
        let! allKeys  = loadAllLedgerKeys ()
        let  range    = CalendarDateRangeWorkflow.derive getCurrentTime window inWindow

        // THE BIND THAT MATTERS. A failed read short-circuits here and the diff below is never
        // reached. There is no `|> Result.defaultValue []` anywhere in this file, and a test
        // asserts a failed read produces Error rather than a plan.  Friction #18 (b).
        let! events = listCalendarEvents account.Id calendarId range

        return DiffInvoicesAgainstCalendarWorkflow.diff { InWindow = inWindow; AllLedgerKeys = allKeys } events
    }
```

### Migration

| Timestamp | Name | Creates |
| --- | --- | --- |
| `20260809000010` | `CreateInvoiceCalendarEventsTable` | `InvoiceCalendarEvents(Id, InvoiceId FK → Invoices.Id ON DELETE CASCADE, GoogleAccountId, CalendarId, EventId, LastSyncedAt)` + unique index on `InvoiceId` |

**This table is history, not truth.** A sync record is a fact about *an invoice*, so it belongs on
this side rather than in the Google integration's store — but it also means the app has an opinion
about what is on a calendar that can go stale if someone deletes an event by hand. **The calendar
remains the source of truth for the diff.** If this table and the calendar disagree, the calendar is
right and the table is out of date. Its actual job is diagnostic: *when did we last touch this event,
and on whose calendar?*

---

## Sequence diagrams

### Building a plan — and the two guards

```
InvoicesPage        InvoiceSyncApi         buildPlan                    Google
     │ "Preview sync"    │                     │
     ├──────────────────►├────────────────────►│ account.DefaultInvoiceCalendar
     │                   │                     │   None → NoDefaultCalendar, button DISABLED (Q2.11)
     │                   │                     ├ loadInvoices (windowed)      → InWindow
     │                   │                     ├ loadAllLedgerKeys ()         → AllLedgerKeys
     │                   │                     │        ▲
     │                   │                     │        └─ GUARD (a): every key in the ledger,
     │                   │                     │           windowed or not. "outside the window"
     │                   │                     │           and "gone from the ledger" are now
     │                   │                     │           different questions, structurally.
     │                   │                     ├ derive CalendarDateRange
     │                   │                     │        [today-N, today+N], stretched forward to
     │                   │                     │        cover the latest due date in view
     │                   │                     ├ listCalendarEvents ────────► Events.list
     │                   │                     │        privateExtendedProperty=mydogsbody.invoice
     │                   │                     │   Error → ABORT. NO PLAN.        ← GUARD (b)
     │                   │                     │   ► never `defaultValue []`
     │                   │                     └ diff (PURE)
     │◄─ SyncAction list ──────────────────────┤
     └─ show the plan. If it contains ANY delete → require confirmation (Q2.13)
```

### The diff — table form

```
For each invoice in InWindow:
    event with matching SyncKey?
        no                                → CreateEvent invoice
        yes, and title+date agree         → LeaveAlone eventId
        yes, and either disagrees         → UpdateEvent (eventId, invoice)

For each event with a SyncKey:
    key present in AllLedgerKeys?
        yes  → already handled above, or the invoice is outside the window → NOTHING
                                                       ▲
                                     GUARD (a): outside the window is NOT gone.
                                     Narrowing 180 → 7 produces ZERO deletes.
        no   → DeleteEvent (eventId, key)      ← the ONLY path that produces a delete

For each event with NO SyncKey, or an unparseable one:
        → reported as an orphan needing attention. NEVER a deletion candidate.
```

### Executing — partial failure and already-gone

```
SyncInvoicesToCalendarWorkflow plan
   │
   ├ LeaveAlone      → Skipped.  NO API CALL AT ALL.
   │
   ├ CreateEvent     → createCalendarEvent (stamps the extended property)
   │                     ok    → markSynced        → Created
   │                     error → Failed, CONTINUE
   │
   ├ UpdateEvent     → updateCalendarEvent (rewrites title AND date, unconditionally — Q2.14)
   │                     ok                   → refresh timestamp → Updated
   │                     EventNoLongerExists  → AlreadyGone   ← SUCCESS, not a failure:
   │                     error → Failed, CONTINUE               the calendar already agrees
   │
   ├ DeleteEvent     → deleteCalendarEvent
   │                     ok                   → clearSyncRecord → Deleted
   │                     EventNoLongerExists  → clearSyncRecord → AlreadyGone
   │                     error → Failed, CONTINUE
   │
   └ CalendarNoLongerExists at any point → STOP the batch and report:
       every remaining action would fail the same way, and continuing just produces noise.
```

### Idempotency — the bar Q2.6 raised

```
run 1:  plan = [Create; Create; Create]   → 3 API calls
run 2:  plan = [LeaveAlone; LeaveAlone; LeaveAlone]   → 0 API calls

  ► NOT "creates no duplicates" - that was the insert-only bar.
  ► With updates in play, a second run over unchanged data must produce nothing but LeaveAlone,
    or every run quietly rewrites every event.
  ► Asserted by executing a plan, re-deriving one over the resulting state, and checking BOTH
    that every action is LeaveAlone AND that a recording fake saw zero calls.
```

---

## Error-handling approach

`CalendarError` in the domain, `MyDogsbodyException` in the outer ring, meeting in
`InvoiceSyncApiFactory`.

The distinctions that matter here:

| Situation | Treated as |
| --- | --- |
| `EventNoLongerExists` on an update or delete | **Success** (`AlreadyGone`). The calendar already agrees with the target state |
| A single create or update failing | `Failed` for that row; **the batch continues** (Q2.8) |
| `CalendarNoLongerExists` | **Stops the batch.** Every remaining action would fail identically |
| `NotAuthorised` mid-batch | **Stops the batch**, reports it, leaves sync records consistent with what actually happened |
| `CalendarRateLimited` | Reported **distinctly** from a permission failure — different instruction to the user |
| A failed **read** | **No plan is produced at all.** Never an empty plan, never a plan of deletes |

Expected and unlogged: `NoDefaultCalendar`, `EventNoLongerExists`, `NotAuthorised`.
Logged once: `EventRejected`, `CalendarUnreachable`, `CalendarRateLimited`, `GoogleStoreFailed`,
`InvoiceStoreFailed`.

```
ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listEvents / createEvent
                                                               / updateEvent / deleteEvent
ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.*
ActionNames.MyDogsbody.Startup.InvoiceSyncApi.*
```

---

## Testing strategy

### The two headline tests

Both against the **pure** diff, where they cost nothing:

```fsharp
[<Fact; Trait("Level", "Unit")>]
let ``diff produces no delete for an invoice that is merely outside the window`` () =
    // 180 days of invoices in the ledger; the window narrowed to 7 so InWindow holds one.
    // Events exist for all of them.
    let snapshot = { InWindow = [ recentInvoice ]; AllLedgerKeys = allSixMonthsOfKeys }
    let plan = diff snapshot allSixMonthsOfEvents
    plan |> List.filter isDelete |> should be Empty
    // ► the single most important test in this change.

[<Fact; Trait("Level", "Unit")>]
let ``a failed calendar read produces no plan`` () =
    let listEvents : ListCalendarEvents = fun _ _ _ -> Error (CalendarUnreachable "token expired")
    match buildPlan getCurrentTime listEvents loadInvoices loadAllKeys account window with
    | Error (CalendarUnreachable _) -> ()
    | Ok plan -> failwith $"produced a plan of {plan.Length} actions from a failed read"
    | Error other -> failwith $"wrong error: {other}"
```

A third, cheap and worth having: **a grep-style test or a review check that
`Result.defaultValue` appears nowhere in the sync files.** Hazard (b) has exactly one way in.

### Unit

- The diff, table-driven over `(ledger, events) → expected plan`, every action's payload asserted:
  create for a missing event; update when the title differs; update when the date differs; leave
  alone when both agree; delete only for a key absent from `AllLedgerKeys`; an event with no sync key
  reported as an orphan and **never** a delete; an event with an unparseable key likewise.
- `InvoiceSyncKey.derive` is stable, and `parse (value (derive a b)) = Ok (derive a b)`.
- `CalendarDateRangeWorkflow`: mirrored around today; **stretched forward when an invoice in view
  falls due beyond it** — a supplier on 60-day terms inside a 14-day window; never produces a range
  excluding an invoice in view.
- `UploadableInvoice.ofStored` returns `None` for an invoice with no due date, and the sync workflow
  **cannot be called with one** — a type error, not a test.
- `SyncInvoicesToCalendarWorkflow`: leave-alone makes **no call** (recording fake asserts zero);
  a failure mid-batch continues and reports per action; `EventNoLongerExists` is `AlreadyGone`, not
  `Failed`; `CalendarNoLongerExists` stops the batch; `markSynced` after a create, `clearSyncRecord`
  after a delete.
- **Idempotency**: execute, re-derive, assert every action is `LeaveAlone` **and** the recording fake
  saw zero calls on the second pass.

### Integration

`InvoiceCalendarEventStore` against a real temp SQLite database; the migration's `Up`/`Down`; the
cascade when an invoice is deleted; the unique index on `InvoiceId`.

### Contract — friction #2 again

The four event dependency types get shared suites run against every fake **and** against the real
adapter over a stubbed `HttpMessageHandler`. Recorded responses: an event list with the extended
property present; one with it absent; a paged list; `404` on update and on delete →
`EventNoLongerExists`; `403`; `429`; `410` on a deleted calendar. **Live verification is manual and
recorded in the change description**, exactly as change #6 established.

### E2E

Create → the event appears in the plan, executes, and the row shows up to date; update → the plan
shows it and the row shows changed beforehand; **delete → the plan shows it and the button requires
confirmation**; leave-alone → the plan is empty and the button says so; a partial failure → per-row
outcomes; a not-ready account → the button is disabled with its reason; selection → ticking rows
limits the plan, and a rescan clears the selection.

### Manual verification — required, and it must include a delete

Against a real Google calendar: create, update, delete, and a second sync making no calls. **The
delete must be exercised by hand**, because it is the one operation whose failure mode the suite
cannot fully model. Record what was run and what was observed.

---

## Decisions taken

1. **`LedgerSnapshot` carries the whole ledger's key set.** Hazard (a) becomes a type-level
   distinction rather than a condition someone remembered to write. This is the single most important
   decision in the change.
2. **The calendar read is bound before the pure diff, and nothing defaults a failed read to `[]`.**
   Hazard (b) has one way in, and it is closed at one line.
3. **`DeleteEvent` carries the sync key as well as the event id.** So the plan preview can say
   *which invoice* an event belonged to, and a delete is reviewable rather than an opaque id.
4. **An event with no sync key, or an unparseable one, is an orphan needing attention — never a
   deletion candidate.** The app deletes only what it can prove it created.
5. **The invoice table shows three per-row states**, not four: up to date, missing, changed.
   **Orphaned is not a per-row state** because an orphaned event has no invoice row to attach it to;
   it appears in the plan and in an orphaned-events view. *(A refinement of the pre-proposal's "four
   states".)*
6. **`InvoiceSyncApi` is a third API record**, separate from change #4's `InvoiceApi` and
   `ScanWindowApi`. Same reasoning: a surface should not have to take the whole invoice API to do one
   job.
7. **An update rewrites title and date unconditionally** (Q2.14), and the page says so. The extended
   property was chosen so a rename would not cause a duplicate; it now also means a rename gets
   reverted. Defensible because a title disagreeing with the ledger is worse than a lost edit — **but
   the page must not pretend otherwise.**
8. **A duplicate event for one invoice is reported, not deleted.** Updating the first and reporting
   the second is recoverable; deleting the second automatically is not.
9. **`CalendarNoLongerExists` stops the batch**; a single `EventRejected` does not. One means every
   remaining action fails identically; the other means one row had a problem.

---

## Risks

| Risk | Handling |
| --- | --- |
| **Friction #18 (a) — deleting on window absence.** The worst thing this feature could possibly do | `LedgerSnapshot` makes it a type-level distinction, and the headline unit test asserts it directly |
| **Friction #18 (b) — deleting on a failed read** | The read binds before the diff; no defaulting anywhere; a unit test asserts a failed read yields `Error`, and a check confirms `Result.defaultValue` appears nowhere in these files |
| **Friction #19 — the calendar may stay mostly empty.** Only ~12% of measured invoices state a due date; `DateFromField` takes it to ~39% | Change #4 records the **real** coverage. This change's value depends on that number more than on anything in its own scope, and the page shows non-uploadable invoices with their reason rather than hiding them |
| **Friction #2 — contract tests against a network service** | Stub-backed suites plus recorded manual verification, **including a real delete** |
| **Friction #1 — blocking on async calls in a batch** | This is the change where a batch first exists, and change #6 named it as the condition for revisiting. If the interface feels stuck, take FsToolkit.ErrorHandling for `asyncResult` |
| **A second sync quietly rewrites every event** | The idempotency test asserts both an all-`LeaveAlone` plan **and** zero calls on a recording fake |
| **An event added by hand is duplicated** | Accepted and stated: no extended property means the diff cannot see it. The page must not claim to detect "any event for this invoice" |
| **A token expires mid-batch** | The batch stops, reports, and leaves sync records consistent with what actually happened — asserted |
