module MyDogsbody.Tests.Contracts.InvoiceCalendarEventStoreDependencyContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup

/// `MarkSynced`, `ClearSyncRecord` and `LoadAllLedgerKeys` - the three storage-facing dependency
/// function types `InvoiceSyncApiFactory.createInvoiceSyncApi` binds directly against
/// `InvoiceCalendarEventStore` (unlike `GoogleAccountApiFactory`'s `bindListCalendarEvents` and its
/// siblings, `InvoiceSyncApiFactory` exposes no separate public `bind*` function for any of these
/// three - inside `createInvoiceSyncApi` each is one line: the real store function piped through
/// `Result.mapError InvoiceApiMappers.toInvoiceError`). "real binding" below is that same one line,
/// reproduced with the identical functions the factory itself calls - not a copy of translation
/// logic, since `toInvoiceError` is `InvoiceApiMappers.toInvoiceError` itself, called directly.

let private handleError = HandleErrorBuilder(fun _ -> ())

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private withSchema (test: DatabaseContext -> unit) =
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
let private eventIdValue value = CalendarEventId.create value |> orFail
let private getCurrentTime () = DateTime(2026, 6, 1, 9, 0, 0)

let private realMarkSynced (context: DatabaseContext) : MarkSynced =
    fun invoiceId accId calId evId ->
        InvoiceCalendarEventStore.markSynced handleError context.GetDatabaseConnection getCurrentTime invoiceId accId calId evId
        |> Result.mapError InvoiceApiMappers.toInvoiceError

let private realClearSyncRecord (context: DatabaseContext) : ClearSyncRecord =
    fun evId ->
        InvoiceCalendarEventStore.clearSyncRecord handleError context.GetDatabaseConnection evId
        |> Result.mapError InvoiceApiMappers.toInvoiceError

let private realLoadAllLedgerKeys (context: DatabaseContext) : LoadAllLedgerKeys =
    fun () ->
        InvoiceCalendarEventStore.loadAllLedgerKeys handleError context.GetDatabaseConnection context.GetInvoices ()
        |> Result.mapError InvoiceApiMappers.toInvoiceError

let private realLoadSyncRecords (context: DatabaseContext) () =
    InvoiceCalendarEventStore.loadSyncRecords handleError context.GetDatabaseConnection context.GetInvoiceCalendarEvents ()
    |> Result.mapError InvoiceApiMappers.toInvoiceError

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real binding" |]; [| box "in-memory fake" |] ]

// ---------- MarkSynced / ClearSyncRecord: shared suite ----------

let private withMarkSyncedImplementation
    (name: string)
    (test:
        MarkSynced
            -> ClearSyncRecord
            -> (unit -> Result<(InvoiceId * GoogleAccountId * CalendarId * CalendarEventId * DateTime) list, InvoiceError>)
            -> InvoiceId
            -> unit)
    =
    match name with
    | "real binding" ->
        withSchema (fun context ->
            let supplierId = insertSupplier context
            let templateId = insertTemplate context supplierId
            let invoiceRowId = insertInvoice context supplierId templateId "INV-1"
            let invoiceId = InvoiceId.create (string invoiceRowId) |> orFail
            test (realMarkSynced context) (realClearSyncRecord context) (realLoadSyncRecords context) invoiceId)
    | "in-memory fake" ->
        let invoiceId = InvoiceId.create "1" |> orFail
        let records = System.Collections.Generic.Dictionary<InvoiceId, GoogleAccountId * CalendarId * CalendarEventId * DateTime>()

        let markSynced: MarkSynced =
            fun invId accId calId evId ->
                records.[invId] <- (accId, calId, evId, getCurrentTime ())
                Ok()

        let clearSyncRecord: ClearSyncRecord =
            fun evId ->
                for entry in records |> Seq.toList do
                    let (_, _, storedEventId, _) = entry.Value
                    if storedEventId = evId then records.Remove entry.Key |> ignore

                Ok()

        let loadSyncRecords () =
            records
            |> Seq.map (fun entry ->
                let (accId, calId, evId, syncedAt) = entry.Value
                entry.Key, accId, calId, evId, syncedAt)
            |> Seq.toList
            |> Ok

        test markSynced clearSyncRecord loadSyncRecords invoiceId
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``markSynced records every field, readable back afterwards`` (implementation: string) =
    withMarkSyncedImplementation implementation (fun markSynced _clearSyncRecord loadSyncRecords invoiceId ->
        markSynced invoiceId accountId calendarId (eventIdValue "evt-1") |> orFail

        match loadSyncRecords () |> orFail with
        | [ (recordedInvoiceId, recordedAccountId, recordedCalendarId, recordedEventId, _) ] ->
            Assert.Equal(invoiceId, recordedInvoiceId)
            Assert.Equal(accountId, recordedAccountId)
            Assert.Equal(calendarId, recordedCalendarId)
            Assert.Equal(eventIdValue "evt-1", recordedEventId)
        | other -> Assert.Fail($"Expected exactly one sync record, got {List.length other}"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``markSynced twice for the same invoice updates rather than duplicating`` (implementation: string) =
    withMarkSyncedImplementation implementation (fun markSynced _clearSyncRecord loadSyncRecords invoiceId ->
        markSynced invoiceId accountId calendarId (eventIdValue "evt-1") |> orFail
        markSynced invoiceId accountId calendarId (eventIdValue "evt-2") |> orFail

        match loadSyncRecords () |> orFail with
        | [ (_, _, _, recordedEventId, _) ] -> Assert.Equal(eventIdValue "evt-2", recordedEventId)
        | other -> Assert.Fail($"Expected exactly one (updated, not duplicated) sync record, got {List.length other}"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``clearSyncRecord removes the row by event id`` (implementation: string) =
    withMarkSyncedImplementation implementation (fun markSynced clearSyncRecord loadSyncRecords invoiceId ->
        markSynced invoiceId accountId calendarId (eventIdValue "evt-1") |> orFail
        clearSyncRecord (eventIdValue "evt-1") |> orFail
        Assert.Empty(loadSyncRecords () |> orFail))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``clearSyncRecord on an already-gone row is idempotent`` (implementation: string) =
    withMarkSyncedImplementation implementation (fun _markSynced clearSyncRecord _loadSyncRecords _invoiceId ->
        clearSyncRecord (eventIdValue "never-existed") |> orFail |> ignore)

// ---------- LoadAllLedgerKeys: shared suite ----------

let private withLoadAllLedgerKeysImplementation
    (name: string)
    (test: LoadAllLedgerKeys -> (string * string) list -> unit)
    =
    match name with
    | "real binding" ->
        withSchema (fun context ->
            let supplierId = insertSupplier context
            let templateId = insertTemplate context supplierId
            insertInvoice context supplierId templateId "INV-RECENT" |> ignore
            insertInvoice context supplierId templateId "INV-FROM-LAST-YEAR" |> ignore
            test (realLoadAllLedgerKeys context) [ string supplierId, "INV-RECENT"; string supplierId, "INV-FROM-LAST-YEAR" ])
    | "in-memory fake" ->
        let ledgerRows = [ "1", "INV-RECENT"; "1", "INV-FROM-LAST-YEAR" ]

        let loadAllLedgerKeys: LoadAllLedgerKeys =
            fun () ->
                ledgerRows
                |> List.map (fun (rawSupplierId, reference) ->
                    InvoiceSyncKey.derive (SupplierId.create rawSupplierId |> orFail) (InvoiceReference.create reference |> orFail))
                |> Set.ofList
                |> Ok

        test loadAllLedgerKeys [ "1", "INV-RECENT"; "1", "INV-FROM-LAST-YEAR" ]
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``loadAllLedgerKeys returns every key in the ledger, ignoring any window`` (implementation: string) =
    withLoadAllLedgerKeysImplementation implementation (fun loadAllLedgerKeys expectedRows ->
        let expectedKeys =
            expectedRows
            |> List.map (fun (rawSupplierId, reference) ->
                InvoiceSyncKey.derive (SupplierId.create rawSupplierId |> orFail) (InvoiceReference.create reference |> orFail))
            |> Set.ofList

        Assert.Equal<Set<InvoiceSyncKey>>(expectedKeys, loadAllLedgerKeys () |> orFail))

[<Fact; Trait("Level", "Contract")>]
let ``the real binding returns an empty set for an empty ledger`` () =
    withSchema (fun context -> Assert.Empty(realLoadAllLedgerKeys context () |> orFail))
