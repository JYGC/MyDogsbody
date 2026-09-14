/// The InvoiceCalendarEvents adapter: markSynced, clearSyncRecord, loadSyncRecords and
/// loadAllLedgerKeys. This table is history, not truth (design.md) - the calendar itself remains
/// the source of truth for DiffInvoicesAgainstCalendarWorkflow.diff; this store's job is
/// diagnostic bookkeeping plus, in loadAllLedgerKeys, the one query hazard (a)'s guard depends on.
///
/// Outer ring - dependencies first, input last, Result<'T, MyDogsbodyException> out, written
/// with handleError. Domain error types are not named here; the composition root translates.
module MyDogsbody.Database.InvoiceCalendarEventStore

open System
open System.Globalization
open Microsoft.Data.Sqlite
open Dapper
open Dapper.FSharp.SQLite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Database.Models

let private runSync (task: System.Threading.Tasks.Task<'T>) : 'T =
    task |> Async.AwaitTask |> Async.RunSynchronously

let private invariant = CultureInfo.InvariantCulture

let private orRaise (what: string) (result: Result<'T, string>) : 'T =
    match result with
    | Ok value -> value
    | Error reason -> raise (InvalidOperationException $"Stored {what} is unusable: {reason}")

let private withConnection (connection: SqliteConnection) (work: unit -> 'T) : 'T =
    connection.Open()
    try work () finally connection.Close()

/// Upsert on the unique InvoiceId index (task 6.1): one invoice syncs to at most one event, so a
/// re-sync (an UpdateEvent outcome) refreshes the same row rather than adding a second.
let markSynced
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getCurrentTime: unit -> DateTime)
    (invoiceId: InvoiceId)
    (accountId: GoogleAccountId)
    (calendarId: CalendarId)
    (eventId: CalendarEventId)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.markSynced

    handleError {
        try
            let connection = getConnection ()
            let rowId = int (InvoiceId.value invoiceId)
            let lastSyncedAt = (getCurrentTime ()).ToString("o", invariant)

            withConnection connection (fun () ->
                connection.ExecuteAsync(
                    "INSERT INTO InvoiceCalendarEvents (InvoiceId, GoogleAccountId, CalendarId, EventId, LastSyncedAt)
                     VALUES (@InvoiceId, @GoogleAccountId, @CalendarId, @EventId, @LastSyncedAt)
                     ON CONFLICT (InvoiceId) DO UPDATE SET
                        GoogleAccountId = excluded.GoogleAccountId,
                        CalendarId = excluded.CalendarId,
                        EventId = excluded.EventId,
                        LastSyncedAt = excluded.LastSyncedAt;",
                    {|
                        InvoiceId = rowId
                        GoogleAccountId = GoogleAccountId.value accountId
                        CalendarId = CalendarId.value calendarId
                        EventId = CalendarEventId.value eventId
                        LastSyncedAt = lastSyncedAt
                    |}
                )
                |> runSync
                |> ignore)

            return ()
        with caughtException ->
            return! MyDogsbodyException(action, "Failed to record the calendar sync for this invoice.", caughtException)
    }

/// Keyed by CalendarEventId, not InvoiceId - see CalendarTypes.fs's ClearSyncRecord doc for why:
/// by the time this is called the invoice is already gone (that is the whole reason its event is
/// being deleted), so there is no InvoiceId left to call with. Idempotent: a row that is already
/// gone (the common case, since the migration's ON DELETE CASCADE usually beats this call to it)
/// deletes zero rows without error.
let clearSyncRecord
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (eventId: CalendarEventId)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.clearSyncRecord

    handleError {
        try
            let connection = getConnection ()

            withConnection connection (fun () ->
                connection.ExecuteAsync(
                    "DELETE FROM InvoiceCalendarEvents WHERE EventId = @EventId;",
                    {| EventId = CalendarEventId.value eventId |}
                )
                |> runSync
                |> ignore)

            return ()
        with caughtException ->
            return! MyDogsbodyException(action, "Failed to clear the calendar sync record.", caughtException)
    }

/// Every InvoiceSyncKey the ledger currently holds, ignoring any scan window entirely - hazard
/// (a)'s structural guard depends on this reading the WHOLE Invoices table, never a windowed
/// LoadInvoices result standing in for it.
let loadAllLedgerKeys
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoiceRecordsQuerySource: unit -> QuerySource<InvoiceRecord>)
    ()
    : Result<Set<InvoiceSyncKey>, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.loadAllLedgerKeys

    handleError {
        try
            let connection = getConnection ()

            let rows =
                withConnection connection (fun () ->
                    select {
                        for invoiceRow in getInvoiceRecordsQuerySource () do
                        selectAll
                    }
                    |> connection.SelectAsync<InvoiceRecord>
                    |> runSync
                    |> Seq.toList)

            return rows |> List.map (InvoiceRecordMappers.toInvoiceSyncKey >> orRaise "invoice") |> Set.ofList
        with caughtException ->
            return! MyDogsbodyException(action, "Failed to load the ledger's sync keys.", caughtException)
    }

/// Diagnostic reads only - "when did we last touch this event, and on whose calendar?" (design.md).
/// No domain workflow declares a dependency type for this; it is read directly by the composition
/// root for the orphaned-events view, the same way GoogleAccountApiFactory.getCalendarsForWith
/// reads ListCalendars directly with no workflow of its own.
let loadSyncRecords
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoiceCalendarEventRecordsQuerySource: unit -> QuerySource<InvoiceCalendarEventRecord>)
    ()
    : Result<(InvoiceId * GoogleAccountId * CalendarId * CalendarEventId * DateTime) list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.loadSyncRecords

    handleError {
        try
            let connection = getConnection ()

            let rows =
                withConnection connection (fun () ->
                    select {
                        for syncRow in getInvoiceCalendarEventRecordsQuerySource () do
                        selectAll
                    }
                    |> connection.SelectAsync<InvoiceCalendarEventRecord>
                    |> runSync
                    |> Seq.toList)

            return rows |> List.map (InvoiceRecordMappers.toSyncRecord >> orRaise "calendar sync record")
        with caughtException ->
            return! MyDogsbodyException(action, "Failed to load calendar sync records.", caughtException)
    }
