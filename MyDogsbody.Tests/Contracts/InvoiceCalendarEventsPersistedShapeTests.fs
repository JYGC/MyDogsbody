module MyDogsbody.Tests.Contracts.InvoiceCalendarEventsPersistedShapeTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Database.Migrations

// Same rationale as InvoicePersistedShapeTests.fs: SQLite is not schemaless, but a Dapper.FSharp
// record field renamed without a matching migration fails only at run time - so the persisted
// column names for InvoiceCalendarEvents (task 6.1's migration) are asserted here against the
// table schema the migrations actually produce, not just by round-tripping an object.

let private withSchema (test: string -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={path};Pooling=False"
    MigrationSetup.setupMigrations connectionString

    try
        test connectionString
    finally
        try File.Delete path with _ -> ()

let private columns (connectionString: string) (table: string) : string list =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{table}')"
    use reader = command.ExecuteReader()

    [ while reader.Read() do
          yield reader.GetString 1 ]

[<Fact; Trait("Level", "Contract")>]
let ``the persisted columns for InvoiceCalendarEvents are exactly as documented`` () =
    withSchema (fun connectionString ->
        Assert.Equal<string list>(
            [ "Id"; "InvoiceId"; "GoogleAccountId"; "CalendarId"; "EventId"; "LastSyncedAt" ],
            columns connectionString "InvoiceCalendarEvents"
        ))

[<Fact; Trait("Level", "Contract")>]
let ``InvoiceCalendarEventRecord has a field for every persisted column`` () =
    // A field renamed on the F# record without a migration would leave the column orphaned; a
    // column added without touching the record would never be read.
    let recordFields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields typeof<MyDogsbody.Database.Models.InvoiceCalendarEventRecord>
        |> Array.map (fun property -> property.Name)
        |> Array.toList
        |> List.sort

    withSchema (fun connectionString ->
        Assert.Equal<string list>(columns connectionString "InvoiceCalendarEvents" |> List.sort, recordFields))

[<Fact; Trait("Level", "Contract")>]
let ``deleting an invoice cascades to its InvoiceCalendarEvents row`` () =
    // task 6.1: SyncInvoicesToCalendarWorkflow's own DeleteEvent case relies on the sync record
    // already being gone by the time ClearSyncRecord runs (CalendarTypes.fs's own doc on
    // ClearSyncRecord) - which only holds if this foreign key genuinely cascades.
    withSchema (fun connectionString ->
        use connection = new SqliteConnection($"{connectionString};Foreign Keys=True")
        connection.Open()

        use insertSupplier = connection.CreateCommand()
        insertSupplier.CommandText <- "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30); SELECT last_insert_rowid();"
        let supplierId = Convert.ToInt32(insertSupplier.ExecuteScalar())

        use insertTemplate = connection.CreateCommand()

        insertTemplate.CommandText <-
            "INSERT INTO InvoiceTemplates (SupplierId, Name, DocumentPart, AttachmentFormat, Position)
             VALUES (@supplierId, 'T', 'AnyPart', NULL, 0); SELECT last_insert_rowid();"

        insertTemplate.Parameters.AddWithValue("@supplierId", supplierId) |> ignore
        let templateId = Convert.ToInt32(insertTemplate.ExecuteScalar())

        use insertInvoice = connection.CreateCommand()

        insertInvoice.CommandText <-
            "INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, MessageReceivedAt, ScannedAt)
             VALUES (@supplierId, @templateId, 'INV-1', '10.00', 'AUD', NULL, '2026-06-15', 'msg', '2026-05-20T00:00:00.0000000', '2026-06-01T00:00:00.0000000');
             SELECT last_insert_rowid();"

        insertInvoice.Parameters.AddWithValue("@supplierId", supplierId) |> ignore
        insertInvoice.Parameters.AddWithValue("@templateId", templateId) |> ignore
        let invoiceId = Convert.ToInt32(insertInvoice.ExecuteScalar())

        use insertSyncRecord = connection.CreateCommand()

        insertSyncRecord.CommandText <-
            "INSERT INTO InvoiceCalendarEvents (InvoiceId, GoogleAccountId, CalendarId, EventId, LastSyncedAt)
             VALUES (@invoiceId, 'acct-1', 'cal-1', 'evt-1', '2026-06-01T00:00:00.0000000');"

        insertSyncRecord.Parameters.AddWithValue("@invoiceId", invoiceId) |> ignore
        insertSyncRecord.ExecuteNonQuery() |> ignore

        use deleteInvoice = connection.CreateCommand()
        deleteInvoice.CommandText <- "DELETE FROM Invoices WHERE Id = @invoiceId;"
        deleteInvoice.Parameters.AddWithValue("@invoiceId", invoiceId) |> ignore
        deleteInvoice.ExecuteNonQuery() |> ignore

        use countRemaining = connection.CreateCommand()
        countRemaining.CommandText <- "SELECT COUNT(*) FROM InvoiceCalendarEvents WHERE InvoiceId = @invoiceId;"
        countRemaining.Parameters.AddWithValue("@invoiceId", invoiceId) |> ignore

        Assert.Equal(0L, Convert.ToInt64(countRemaining.ExecuteScalar())))
