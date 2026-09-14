module MyDogsbody.Tests.Database.InvoiceCalendarEventStoreTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Database
open MyDogsbody.Database.Migrations

let private handleError = HandleErrorBuilder(fun _ -> ())

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

/// `;Pooling=False` on every connection string this test builds, matching
/// `DatabaseContextSetup.createDatabaseContext` - see CLAUDE-project.md -> Testing -> Integration.
let private withStore (test: DatabaseContext -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={path};Pooling=False"
    let context = DatabaseContextSetup.createDatabaseContext path

    try
        test context
    finally
        context.Dispose()
        try File.Delete path with _ -> ()

let private insertSupplier (context: DatabaseContext) : int =
    let connection = context.GetDatabaseConnection()
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30); SELECT last_insert_rowid();"
    let id = Convert.ToInt32(command.ExecuteScalar())
    connection.Close()
    id

let private insertTemplate (context: DatabaseContext) (supplierId: int) : int =
    let connection = context.GetDatabaseConnection()
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        "INSERT INTO InvoiceTemplates (SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (@supplierId, 'T', 'AnyPart', NULL, 0); SELECT last_insert_rowid();"
    command.Parameters.AddWithValue("@supplierId", supplierId) |> ignore
    let id = Convert.ToInt32(command.ExecuteScalar())
    connection.Close()
    id

let private insertInvoice (context: DatabaseContext) (supplierId: int) (templateId: int) (reference: string) : int =
    let connection = context.GetDatabaseConnection()
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        "INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, MessageReceivedAt, ScannedAt)
         VALUES (@supplierId, @templateId, @reference, '10.00', 'AUD', NULL, '2026-06-15', 'msg', '2026-05-20T00:00:00.0000000', '2026-06-01T00:00:00.0000000');
         SELECT last_insert_rowid();"
    command.Parameters.AddWithValue("@supplierId", supplierId) |> ignore
    command.Parameters.AddWithValue("@templateId", templateId) |> ignore
    command.Parameters.AddWithValue("@reference", reference) |> ignore
    let id = Convert.ToInt32(command.ExecuteScalar())
    connection.Close()
    id

let private accountId = GoogleAccountId.create "acct-1" |> orFail
let private calendarId = CalendarId.create "cal-1" |> orFail
let private eventId value = CalendarEventId.create value |> orFail
let private getCurrentTime () = DateTime(2026, 6, 1, 9, 0, 0)

// ================================================================================================
// Integration
// ================================================================================================

[<Fact; Trait("Level", "Integration")>]
let ``markSynced then loadSyncRecords round-trips every field`` () =
    withStore (fun context ->
        let supplierId = insertSupplier context
        let templateId = insertTemplate context supplierId
        let invoiceRowId = insertInvoice context supplierId templateId "INV-1"
        let invoiceId = InvoiceId.create (string invoiceRowId) |> orFail

        InvoiceCalendarEventStore.markSynced
            handleError
            context.GetDatabaseConnection
            getCurrentTime
            invoiceId
            accountId
            calendarId
            (eventId "evt-1")
        |> orFail

        let records =
            InvoiceCalendarEventStore.loadSyncRecords handleError context.GetDatabaseConnection context.GetInvoiceCalendarEvents ()
            |> orFail

        match records with
        | [ (recordedInvoiceId, recordedAccountId, recordedCalendarId, recordedEventId, lastSyncedAt) ] ->
            Assert.Equal(invoiceId, recordedInvoiceId)
            Assert.Equal(accountId, recordedAccountId)
            Assert.Equal(calendarId, recordedCalendarId)
            Assert.Equal(eventId "evt-1", recordedEventId)
            Assert.Equal(getCurrentTime (), lastSyncedAt)
        | other -> Assert.Fail($"Expected exactly one sync record, got {List.length other}"))

[<Fact; Trait("Level", "Integration")>]
let ``markSynced twice for the same invoice updates rather than duplicating`` () =
    withStore (fun context ->
        let supplierId = insertSupplier context
        let templateId = insertTemplate context supplierId
        let invoiceRowId = insertInvoice context supplierId templateId "INV-1"
        let invoiceId = InvoiceId.create (string invoiceRowId) |> orFail

        InvoiceCalendarEventStore.markSynced
            handleError context.GetDatabaseConnection getCurrentTime invoiceId accountId calendarId (eventId "evt-1")
        |> orFail

        let laterCalendarId = CalendarId.create "cal-2" |> orFail

        InvoiceCalendarEventStore.markSynced
            handleError context.GetDatabaseConnection getCurrentTime invoiceId accountId laterCalendarId (eventId "evt-2")
        |> orFail

        let records =
            InvoiceCalendarEventStore.loadSyncRecords handleError context.GetDatabaseConnection context.GetInvoiceCalendarEvents ()
            |> orFail

        match records with
        | [ (_, _, recordedCalendarId, recordedEventId, _) ] ->
            Assert.Equal(laterCalendarId, recordedCalendarId)
            Assert.Equal(eventId "evt-2", recordedEventId)
        | other -> Assert.Fail($"Expected exactly one (updated, not duplicated) sync record, got {List.length other}"))

[<Fact; Trait("Level", "Integration")>]
let ``clearSyncRecord removes the row by event id`` () =
    withStore (fun context ->
        let supplierId = insertSupplier context
        let templateId = insertTemplate context supplierId
        let invoiceRowId = insertInvoice context supplierId templateId "INV-1"
        let invoiceId = InvoiceId.create (string invoiceRowId) |> orFail

        InvoiceCalendarEventStore.markSynced
            handleError context.GetDatabaseConnection getCurrentTime invoiceId accountId calendarId (eventId "evt-1")
        |> orFail

        InvoiceCalendarEventStore.clearSyncRecord handleError context.GetDatabaseConnection (eventId "evt-1") |> orFail

        let records =
            InvoiceCalendarEventStore.loadSyncRecords handleError context.GetDatabaseConnection context.GetInvoiceCalendarEvents ()
            |> orFail

        Assert.Empty records)

[<Fact; Trait("Level", "Integration")>]
let ``clearSyncRecord on an already-gone row is idempotent`` () =
    withStore (fun context ->
        InvoiceCalendarEventStore.clearSyncRecord handleError context.GetDatabaseConnection (eventId "never-existed")
        |> orFail
        |> ignore)

[<Fact; Trait("Level", "Integration")>]
let ``loadAllLedgerKeys returns every key in the ledger, ignoring any window`` () =
    // This is the query hazard (a)'s guard depends on: unlike LoadInvoices, this function takes
    // no cutoff at all, so there is no windowed call it could be confused with.
    withStore (fun context ->
        let supplierId = insertSupplier context
        let templateId = insertTemplate context supplierId
        insertInvoice context supplierId templateId "INV-RECENT" |> ignore
        insertInvoice context supplierId templateId "INV-FROM-LAST-YEAR" |> ignore

        let keys =
            InvoiceCalendarEventStore.loadAllLedgerKeys handleError context.GetDatabaseConnection context.GetInvoices ()
            |> orFail

        let expectedSupplierId = SupplierId.create (string supplierId) |> orFail

        let expectedKeys =
            [ "INV-RECENT"; "INV-FROM-LAST-YEAR" ]
            |> List.map (fun reference -> InvoiceSyncKey.derive expectedSupplierId (InvoiceReference.create reference |> orFail))
            |> Set.ofList

        Assert.Equal<Set<InvoiceSyncKey>>(expectedKeys, keys))

[<Fact; Trait("Level", "Integration")>]
let ``loadAllLedgerKeys returns an empty set for an empty ledger`` () =
    withStore (fun context ->
        let keys =
            InvoiceCalendarEventStore.loadAllLedgerKeys handleError context.GetDatabaseConnection context.GetInvoices ()
            |> orFail

        Assert.Empty keys)

// ================================================================================================
// Unit - error paths
// ================================================================================================

let private boom () : SqliteConnection = raise (InvalidOperationException "down")

[<Fact; Trait("Level", "Unit")>]
let ``markSynced reports the declared action and preserves the inner exception`` () =
    match InvoiceCalendarEventStore.markSynced handleError boom getCurrentTime (InvoiceId.create "1" |> orFail) accountId calendarId (eventId "evt-1") with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.markSynced, caughtException.ActionName)
        Assert.Equal("Failed to record the calendar sync for this invoice.", caughtException.Message)
        Assert.IsType<InvalidOperationException>(caughtException.InnerException) |> ignore
    | Ok _ -> Assert.Fail("expected Error")

[<Fact; Trait("Level", "Unit")>]
let ``clearSyncRecord reports the declared action and preserves the inner exception`` () =
    match InvoiceCalendarEventStore.clearSyncRecord handleError boom (eventId "evt-1") with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.clearSyncRecord, caughtException.ActionName)
        Assert.Equal("Failed to clear the calendar sync record.", caughtException.Message)
        Assert.IsType<InvalidOperationException>(caughtException.InnerException) |> ignore
    | Ok _ -> Assert.Fail("expected Error")

[<Fact; Trait("Level", "Unit")>]
let ``loadAllLedgerKeys reports the declared action and preserves the inner exception`` () =
    match InvoiceCalendarEventStore.loadAllLedgerKeys handleError boom (fun () -> failwith "unused") () with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.loadAllLedgerKeys, caughtException.ActionName)
        Assert.Equal("Failed to load the ledger's sync keys.", caughtException.Message)
        Assert.IsType<InvalidOperationException>(caughtException.InnerException) |> ignore
    | Ok _ -> Assert.Fail("expected Error")

[<Fact; Trait("Level", "Unit")>]
let ``loadSyncRecords reports the declared action and preserves the inner exception`` () =
    match InvoiceCalendarEventStore.loadSyncRecords handleError boom (fun () -> failwith "unused") () with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceCalendarEventStore.loadSyncRecords, caughtException.ActionName)
        Assert.Equal("Failed to load calendar sync records.", caughtException.Message)
        Assert.IsType<InvalidOperationException>(caughtException.InnerException) |> ignore
    | Ok _ -> Assert.Fail("expected Error")
