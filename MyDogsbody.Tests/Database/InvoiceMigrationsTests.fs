module MyDogsbody.Tests.Database.InvoiceMigrationsTests

open System
open Microsoft.Data.Sqlite
open Xunit
open MyDogsbody.Database.Migrations
open MyDogsbody.Tests.Database.MigrationTestHelpers

// The migrations are the schema source of truth - a test never writes its own DDL, it calls
// setupMigrations and asserts what that produced (CLAUDE-project.md -> Testing).

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

let private insertInvoice (connectionString: string) (supplierId: int64) (templateId: int64) (reference: string) =
    execParams
        connectionString
        "INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, MessageReceivedAt, ScannedAt)
         VALUES (@supplierId, @templateId, @reference, '10.00', 'AUD', NULL, NULL, 'msg', '2026-05-20T00:00:00.0000000', '2026-06-01T00:00:00.0000000')"
        [ "@supplierId", box supplierId; "@templateId", box templateId; "@reference", box reference ]

// ============================ 6.1 Invoices ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates Invoices with its expected columns`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        Assert.Contains("Invoices", tableNames connectionString)

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "TemplateId"; "Reference"; "Amount"; "Currency"; "IssueDate"; "DueDate"; "SourceMessageId"; "MessageReceivedAt"; "ScannedAt" ],
            columnNames connectionString "Invoices"
        ))

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on (SupplierId, Reference) refuses a duplicate`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplier connectionString
        let templateId = insertTemplate connectionString supplierId
        insertInvoice connectionString supplierId templateId "INV-1"

        Assert.Contains("IX_Invoices_SupplierId_Reference", indexNames connectionString "Invoices")

        let caughtException = Assert.Throws<SqliteException>(fun () -> insertInvoice connectionString supplierId templateId "INV-1")
        Assert.Contains("UNIQUE", caughtException.Message))

[<Fact; Trait("Level", "Integration")>]
let ``the Invoices supplier foreign key rejects an unknown supplier`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplier connectionString
        let templateId = insertTemplate connectionString supplierId
        Assert.Throws<SqliteException>(fun () -> insertInvoice connectionString 999L templateId "INV-X") |> ignore)

/// TemplateId is PROVENANCE, not a relationship - requirements.md asks only that an invoice
/// "record which template produced it", and nothing joins the two: InvoiceRecordMappers reads the
/// column straight back into an opaque TemplateId and InvoiceUiType does not carry one at all.
/// So the column deliberately has no foreign key, exactly as ScanProblems.SupplierId does and for
/// the same stated reason ("the supplier may legitimately be gone ... a diagnostic, not a
/// relationship"). An unknown TemplateId is therefore accepted.
[<Fact; Trait("Level", "Integration")>]
let ``the Invoices table accepts a TemplateId whose template is gone`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplier connectionString
        insertInvoice connectionString supplierId 999L "INV-Y"
        Assert.Equal(1L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM Invoices")))

/// An invoice is a stored fact (Q5.7) and the ledger is what this change exists to keep. Deleting
/// the TEMPLATE that produced one must not take the invoice with it: a template is a parsing
/// recipe, the invoice is the result, and invoice-templates requirements.md line 167 says deleting
/// a template deletes "it and its rules" - not the ledger rows it once produced. There is no
/// tombstone for a cascade, so the rows would be gone silently and permanently.
[<Fact; Trait("Level", "Integration")>]
let ``deleting a template leaves the invoices it produced`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplier connectionString
        let keptTemplate = insertTemplate connectionString supplierId
        let doomedTemplate = insertTemplate connectionString supplierId

        insertInvoice connectionString supplierId doomedTemplate "INV-FROM-DELETED-TEMPLATE"
        insertInvoice connectionString supplierId keptTemplate "INV-FROM-KEPT-TEMPLATE"

        execParams connectionString "DELETE FROM InvoiceTemplates WHERE Id = @templateId" [ "@templateId", box doomedTemplate ]

        Assert.Equal(1L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM InvoiceTemplates"))
        Assert.Equal(2L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM Invoices"))

        let references =
            use connection = new SqliteConnection(connectionString)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT Reference FROM Invoices ORDER BY Reference"
            use reader = command.ExecuteReader()
            [ while reader.Read() do yield reader.GetString 0 ]

        Assert.Equal<string list>([ "INV-FROM-DELETED-TEMPLATE"; "INV-FROM-KEPT-TEMPLATE" ], references))

/// The supplier cascade is deliberate and stays: the domain carries SupplierGone for exactly this,
/// and an invoice whose supplier is gone has no name to render.
[<Fact; Trait("Level", "Integration")>]
let ``deleting a supplier still removes its invoices`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplier connectionString
        let templateId = insertTemplate connectionString supplierId
        insertInvoice connectionString supplierId templateId "INV-1"

        execParams connectionString "DELETE FROM Suppliers WHERE Id = @supplierId" [ "@supplierId", box supplierId ]

        Assert.Equal(0L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM Invoices")))

[<Fact; Trait("Level", "Integration")>]
let ``Down on every change #4 migration removes all five tables, and MigrateUp rebuilds them`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        MigrationSetup.rollbackAll connectionString

        for table in [ "Invoices"; "ScanProblems"; "InvoiceTombstones"; "ScanWindows"; "InvoiceSettings" ] do
            Assert.DoesNotContain(table, tableNames connectionString)

        MigrationSetup.setupMigrations connectionString

        for table in [ "Invoices"; "ScanProblems"; "InvoiceTombstones"; "ScanWindows"; "InvoiceSettings" ] do
            Assert.Contains(table, tableNames connectionString)

        // the seed comes back on a genuine rebuild (VersionInfo was cleared by rollbackAll)
        Assert.Equal(5L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM ScanWindows")))

// ============================ 6.2 ScanProblems ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates ScanProblems with its columns and the SourceMessageId index`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Equal<string list>(
            [ "Id"; "SourceMessageId"; "SupplierId"; "Sender"; "Subject"; "ReceivedAt"; "Cause"; "Detail"; "RecordedAt" ],
            columnNames connectionString "ScanProblems"
        )

        Assert.Contains("IX_ScanProblems_SourceMessageId", indexNames connectionString "ScanProblems"))

// ============================ 6.3 InvoiceTombstones ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates InvoiceTombstones with its columns and a unique (SupplierId, Reference) index`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "Reference"; "DeletedAt" ],
            columnNames connectionString "InvoiceTombstones"
        )

        Assert.Contains("IX_InvoiceTombstones_SupplierId_Reference", indexNames connectionString "InvoiceTombstones")

        let supplierId = insertSupplier connectionString

        let insertTombstone () =
            execParams
                connectionString
                "INSERT INTO InvoiceTombstones (SupplierId, Reference, DeletedAt) VALUES (@supplierId, 'INV-1', '2026-06-01T00:00:00.0000000')"
                [ "@supplierId", box supplierId ]

        insertTombstone ()
        Assert.Throws<SqliteException>(insertTombstone) |> ignore)

// ============================ 6.4 ScanWindows (+ seed) ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp seeds exactly the five starting windows`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        Assert.Equal(5L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM ScanWindows"))

        let days =
            use connection = new SqliteConnection(connectionString)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT Days FROM ScanWindows ORDER BY Days"
            use reader = command.ExecuteReader()
            [ while reader.Read() do yield reader.GetInt32 0 ]

        Assert.Equal<int list>([ 7; 14; 30; 90; 180 ], days))

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on Days refuses a sixth 14`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        Assert.Contains("IX_ScanWindows_Days", indexNames connectionString "ScanWindows")
        Assert.Throws<SqliteException>(fun () -> exec connectionString "INSERT INTO ScanWindows (Days) VALUES (14)") |> ignore)

[<Fact; Trait("Level", "Integration")>]
let ``the ScanWindows Down runs its Delete.FromTable and drops the table cleanly`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        // rollbackTo the version just before ScanWindows: its Down (Delete.FromTable x5, then
        // Delete.Index, then Delete.Table) must run without error - a Delete.FromTable against a
        // row that is not there would still succeed, but a malformed one would throw here.
        MigrationSetup.rollbackToVersion connectionString 20260810000006L
        Assert.DoesNotContain("ScanWindows", tableNames connectionString)
        Assert.Contains("InvoiceTombstones", tableNames connectionString))

[<Fact; Trait("Level", "Integration")>]
let ``re-running migrations after a user deletes a seeded window does not restore it`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        exec connectionString "DELETE FROM ScanWindows WHERE Days = 30"
        Assert.Equal(4L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM ScanWindows"))

        MigrationSetup.setupMigrations connectionString // already applied - the seed does not re-run
        Assert.Equal(4L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM ScanWindows"))
        Assert.Equal(0L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM ScanWindows WHERE Days = 30")))

// ============================ 6.5 InvoiceSettings ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates InvoiceSettings fixed at a single row`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Equal<string list>([ "Id"; "SelectedScanWindowDays" ], columnNames connectionString "InvoiceSettings")

        exec connectionString "INSERT INTO InvoiceSettings (Id, SelectedScanWindowDays) VALUES (1, NULL)"
        // a second row - whether Id 1 (primary key) or Id 2 (check) - is refused
        Assert.Throws<SqliteException>(fun () -> exec connectionString "INSERT INTO InvoiceSettings (Id, SelectedScanWindowDays) VALUES (1, 14)") |> ignore
        Assert.Throws<SqliteException>(fun () -> exec connectionString "INSERT INTO InvoiceSettings (Id, SelectedScanWindowDays) VALUES (2, 14)") |> ignore)

[<Fact; Trait("Level", "Integration")>]
let ``the InvoiceSettings setting column is nullable`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        exec connectionString "INSERT INTO InvoiceSettings (Id) VALUES (1)"
        Assert.Equal(DBNull.Value :> obj, queryScalar connectionString "SELECT SelectedScanWindowDays FROM InvoiceSettings WHERE Id = 1"))

[<Fact; Trait("Level", "Integration")>]
let ``Down reverses the InvoiceSettings migration`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        MigrationSetup.rollbackToVersion connectionString 20260810000007L
        Assert.DoesNotContain("InvoiceSettings", tableNames connectionString)
        Assert.Contains("ScanWindows", tableNames connectionString))
