/// See design.md's "Executing - partial failure and already-gone" sequence for the full derivation.
module MyDogsbody.Domain.Calendar.SyncInvoicesToCalendarWorkflow

open MyDogsbody.Domain.Calendar
open MyDogsbody.Domain.Calendar.DiffInvoicesAgainstCalendarWorkflow

/// `markSynced`/`clearSyncRecord` failures are discarded as values,
/// deliberately, rather than turning a successful calendar write into a reported failure -
/// InvoiceCalendarEvents is history, not truth (design.md), so a failure to update it here is a
/// diagnostic-table nuisance, not a reason to tell the user their sync failed. The same posture
/// `GoogleAccountApiFactory.RemoveAccount` already takes with `removeStoredToken`.
let private executeOne
    (createCalendarEvent: CreateCalendarEvent)
    (updateCalendarEvent: UpdateCalendarEvent)
    (deleteCalendarEvent: DeleteCalendarEvent)
    (markSynced: MarkSynced)
    (clearSyncRecord: ClearSyncRecord)
    (accountId: GoogleAccountId)
    (calendarId: CalendarId)
    (action: SyncAction)
    : SyncOutcome =
    match action with
    | LeaveAlone eventId -> Skipped eventId
    | CreateEvent invoice ->
        let key = InvoiceSyncKey.derive invoice.SupplierId invoice.Reference

        match createCalendarEvent accountId calendarId key (toExpectedEvent invoice) with
        | Ok eventId ->
            markSynced invoice.Id accountId calendarId eventId |> ignore
            Created(invoice.Id, eventId)
        | Error error -> Failed(action, error)
    | UpdateEvent(eventId, invoice) ->
        // Q2.14: rewrites title and date unconditionally - no comparison happens here, `diff`
        // already decided this event disagrees.
        match updateCalendarEvent accountId calendarId eventId (toExpectedEvent invoice) with
        | Ok() ->
            markSynced invoice.Id accountId calendarId eventId |> ignore
            Updated(invoice.Id, eventId)
        | Error(EventNoLongerExists _) ->
            // The calendar already agrees with where we were trying to get to - a success, not a
            // failure. The invoice is still live, so the NEXT sync's diff will see no matching
            // event and produce a fresh CreateEvent for it; nothing to clean up here.
            AlreadyGone eventId
        | Error error -> Failed(action, error)
    | DeleteEvent(eventId, _syncKey) ->
        match deleteCalendarEvent accountId calendarId eventId with
        | Ok() ->
            clearSyncRecord eventId |> ignore
            Deleted eventId
        | Error(EventNoLongerExists _) ->
            clearSyncRecord eventId |> ignore
            AlreadyGone eventId
        | Error error -> Failed(action, error)

/// Every remaining action would fail identically once the calendar itself is gone, or once the
/// account's authorisation has lapsed mid-batch - continuing past either just produces noise, so
/// both stop the batch rather than being reported and continued past like an ordinary per-row
/// failure (Q2.8).
let private stopsTheBatch =
    function
    | CalendarNoLongerExists _
    | NotAuthorised _ -> true
    | _ -> false

/// An ordinary per-action failure is recorded as `Failed` and the batch continues (Q2.8) - the
/// earlier successes stay recorded, because each action's `markSynced`/`clearSyncRecord` call
/// already ran by the time the next one is attempted. `CalendarNoLongerExists` and
/// `NotAuthorised` stop the batch instead: every action after that point is left un-attempted,
/// and the sync records are consistent with exactly what ran.
let executePlan
    (createCalendarEvent: CreateCalendarEvent)
    (updateCalendarEvent: UpdateCalendarEvent)
    (deleteCalendarEvent: DeleteCalendarEvent)
    (markSynced: MarkSynced)
    (clearSyncRecord: ClearSyncRecord)
    (accountId: GoogleAccountId)
    (calendarId: CalendarId)
    (plan: SyncAction list)
    : Result<SyncOutcome list, CalendarError> =
    let executeOne =
        executeOne createCalendarEvent updateCalendarEvent deleteCalendarEvent markSynced clearSyncRecord accountId calendarId

    let rec loop (remaining: SyncAction list) (completed: SyncOutcome list) : SyncOutcome list =
        match remaining with
        | [] -> List.rev completed
        | action :: rest ->
            match executeOne action with
            | Failed(_, error) as outcome when stopsTheBatch error -> List.rev (outcome :: completed)
            | outcome -> loop rest (outcome :: completed)

    Ok(loop plan [])
