module MyDogsbody.Tests.Database.InvoiceCalendarEventsMigrationTests

open System
open Microsoft.Data.Sqlite
open Xunit
open MyDogsbody.Database.Migrations
open MyDogsbody.Tests.Database.MigrationTestHelpers

// Change #7, task 6.1. The migrations are the schema source of truth - a test never writes its
// own DDL, it calls setupMigrations and asserts what that produced.

let private insertSupplier (connectionString: string) : int64 =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30); SELECT last_insert_rowid();"
    Convert.ToInt64(command.ExecuteScalar())

let private insertTemplate (connectionString: string) (supplierId: int64) : int64 =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        "INSERT INTO InvoiceTemplates (SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (@supplierId, 'T', 'AnyPart', NULL, 0); SELECT last_insert_rowid();"
    command.Parameters.AddWithValue("@supplierId", supplierId) |> ignore
    Convert.ToInt64(command.ExecuteScalar())

let private insertInvoice (connectionString: string) (supplierId: int64) (templateId: int64) (reference: string) : int64 =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        "INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, MessageReceivedAt, ScannedAt)
         VALUES (@supplierId, @templateId, @reference, '10.00', 'AUD', NULL, '2026-06-15', 'msg', '2026-05-20T00:00:00.0000000', '2026-06-01T00:00:00.0000000');
         SELECT last_insert_rowid();"
    command.Parameters.AddWithValue("@supplierId", supplierId) |> ignore
    command.Parameters.AddWithValue("@templateId", templateId) |> ignore
    command.Parameters.AddWithValue("@reference", reference) |> ignore
    Convert.ToInt64(command.ExecuteScalar())

let private insertSyncRecord (connectionString: string) (invoiceId: int64) =
    execParams
        connectionString
        "INSERT INTO InvoiceCalendarEvents (InvoiceId, GoogleAccountId, CalendarId, EventId, LastSyncedAt)
         VALUES (@invoiceId, 'acct-1', 'cal-1', 'evt-1', '2026-06-01T00:00:00.0000000')"
        [ "@invoiceId", box invoiceId ]

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates InvoiceCalendarEvents with its expected columns`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        Assert.Contains("InvoiceCalendarEvents", tableNames connectionString)

        Assert.Equal<string list>(
            [ "Id"; "InvoiceId"; "GoogleAccountId"; "CalendarId"; "EventId"; "LastSyncedAt" ],
            columnNames connectionString "InvoiceCalendarEvents"
        ))

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on InvoiceId refuses a second sync record for the same invoice`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplier connectionString
        let templateId = insertTemplate connectionString supplierId
        let invoiceId = insertInvoice connectionString supplierId templateId "INV-1"

        insertSyncRecord connectionString invoiceId

        Assert.Contains("IX_InvoiceCalendarEvents_InvoiceId", indexNames connectionString "InvoiceCalendarEvents")

        let caughtException = Assert.Throws<SqliteException>(fun () -> insertSyncRecord connectionString invoiceId)
        Assert.Contains("UNIQUE", caughtException.Message))

[<Fact; Trait("Level", "Integration")>]
let ``deleting an invoice cascades to its sync record`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplier connectionString
        let templateId = insertTemplate connectionString supplierId
        let invoiceId = insertInvoice connectionString supplierId templateId "INV-1"
        insertSyncRecord connectionString invoiceId

        Assert.Equal(1L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM InvoiceCalendarEvents"))

        execParams connectionString "DELETE FROM Invoices WHERE Id = @invoiceId" [ "@invoiceId", box invoiceId ]

        Assert.Equal(0L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM InvoiceCalendarEvents")))

[<Fact; Trait("Level", "Integration")>]
let ``the InvoiceId foreign key rejects an unknown invoice`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        Assert.Throws<SqliteException>(fun () -> insertSyncRecord connectionString 999L) |> ignore)

[<Fact; Trait("Level", "Integration")>]
let ``Down reverses the InvoiceCalendarEvents migration`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        MigrationSetup.rollbackToVersion connectionString 20260810000008L
        Assert.DoesNotContain("InvoiceCalendarEvents", tableNames connectionString)
        Assert.Contains("InvoiceSettings", tableNames connectionString))
