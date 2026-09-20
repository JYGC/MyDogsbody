/// The heart of the change. See design.md - "The one thing this design is really about": every
/// other workflow in this series can, at worst, write a wrong row to a database the app owns.
/// This one can delete entries from a calendar the app neither owns nor can restore, from a
/// defect in what is normally the safest kind of code - a pure function.
///
/// Two hazards, both closed structurally rather than conditionally:
///   (a) deleting because an invoice is outside the WINDOW rather than gone from the LEDGER -
///       closed by LedgerSnapshot carrying AllLedgerKeys (every key, unwindowed) alongside
///       InWindow, so `diff` is never handed a windowed-only list in the first place.
///   (b) deleting because a READ failed and came back empty - closed by `buildPlan` binding the
///       calendar read in a `result` pipeline BEFORE calling the pure `diff`, so an `Error`
///       short-circuits and `diff` is never reached. There is no defaulting of a failed `Result`
///       anywhere in this file.
module MyDogsbody.Domain.Calendar.DiffInvoicesAgainstCalendarWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar

/// The event content an invoice implies - the ONE function both `diff` (to detect whether an
/// event still agrees with the ledger) and SyncInvoicesToCalendarWorkflow (to create/update the
/// real event) use, so what "the event matches the ledger" cannot drift between the two.
///
/// Built only from what UploadableInvoice itself carries - reference, amount, due date - not the
/// supplier's name, which would need a lookup this pure function cannot make. Requirements.md
/// leaves the exact wording unspecified beyond "title and description as specified"; this is the
/// concrete choice made here.
let toExpectedEvent (invoice: UploadableInvoice) : AllDayEvent =
    { Date = InvoiceDueDate.value invoice.DueDate
      Title = $"Invoice due: {InvoiceReference.value invoice.Reference}"
      Description = $"Amount: {Money.amount invoice.Amount} {Money.currency invoice.Amount}. Synced by MyDogsbody." }

/// Q2.14: an update rewrites both unconditionally when either disagrees. Description is not
/// compared: it carries no information the ledger's own fields don't already put in the title, so
/// it drifting alone is not "disagreement".
let private eventAgreesOnTitleAndDateOnly (expected: AllDayEvent) (actual: AllDayEvent) : bool =
    expected.Date.Date = actual.Date.Date && expected.Title = actual.Title

let private invoiceSyncKey (invoice: UploadableInvoice) : InvoiceSyncKey =
    InvoiceSyncKey.derive invoice.SupplierId invoice.Reference

/// Pure. See design.md's "The diff - table form" for the derivation this implements.
///
/// PURE and takes no dependency parameters - no network, no clock, no store. Everything it needs
/// to answer correctly is already in its two arguments, which is what makes hazard (a) a
/// property of the TYPE it is handed rather than a rule someone had to remember.
let diff (snapshot: LedgerSnapshot) (events: CalendarEvent list) : SyncAction list =
    // An event with no extended property, or one whose value did not parse, has SyncKey = None
    // (the adapter's job, not this function's) and is an orphan needing attention - structurally
    // unable to become a DeleteEvent, since that constructor demands a key it does not have.
    let keyedEvents = events |> List.choose (fun event -> event.SyncKey |> Option.map (fun key -> key, event))

    // Duplicates: when more than one event shares a key, the FIRST (in list order) is the one
    // compared against the ledger; every other is left untouched - neither updated nor deleted.
    // Surfacing "this is a duplicate" is a UI-level concern (the orphaned-events view, phase 8.6),
    // not a fourth SyncAction case design.md does not declare.
    let firstEventByKey =
        keyedEvents
        |> List.fold
            (fun accumulated (key, event) ->
                if Map.containsKey key accumulated then accumulated else Map.add key event accumulated)
            Map.empty

    let actionsForInvoicesInWindow =
        snapshot.InWindow
        |> List.map (fun invoice ->
            let key = invoiceSyncKey invoice

            match Map.tryFind key firstEventByKey with
            | None -> CreateEvent invoice
            | Some event when eventAgreesOnTitleAndDateOnly (toExpectedEvent invoice) event.Event ->
                LeaveAlone event.Id
            | Some event -> UpdateEvent(event.Id, invoice))

    // The ONLY path that produces a delete: a canonical (first-seen) keyed event whose key is
    // absent from AllLedgerKeys ENTIRELY. A key that is merely outside the window is still in
    // AllLedgerKeys (it is every key in the ledger, unwindowed), so it never reaches here - that
    // is hazard (a) closed by construction, not by a condition below remembering to check it.
    let deleteActionsForGoneInvoices =
        firstEventByKey
        |> Map.toList
        |> List.choose (fun (key, event) ->
            if Set.contains key snapshot.AllLedgerKeys then None else Some(DeleteEvent(event.Id, key)))

    actionsForInvoicesInWindow @ deleteActionsForGoneInvoices

/// The plan-building pipeline. Hazard (b) lives or dies on this shape: the calendar read is
/// bound in this `result` pipeline BEFORE `diff` is called, so a failed read short-circuits
/// right here and `diff` is never reached. Nothing in this function defaults a failed `Result` -
/// a check confirms it (task 2.3).
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
            match account.DefaultInvoiceCalendar with
            | Some id -> Ok id
            | None -> Error(NoDefaultCalendar account.Id)

        let cutoff = ScanForInvoicesWorkflow.computeCutoff getCurrentTime window

        let! storedInWindow = loadInvoices (Some cutoff) |> Result.mapError (fun e -> InvoiceStoreFailed $"{e}")
        let inWindow = storedInWindow |> List.choose UploadableInvoice.ofStored

        let! allKeys = loadAllLedgerKeys () |> Result.mapError (fun e -> InvoiceStoreFailed $"{e}")

        let range = CalendarDateRangeWorkflow.derive getCurrentTime window inWindow

        // THE BIND THAT MATTERS. A failed read short-circuits here and `diff` below is never
        // reached. Friction #18 (b).
        let! events = listCalendarEvents account.Id calendarId range

        return diff { InWindow = inWindow; AllLedgerKeys = allKeys } events
    }
