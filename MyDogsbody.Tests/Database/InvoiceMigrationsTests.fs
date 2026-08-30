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
         VALUES (@s, 'T', 'AnyPart', NULL, 0); SELECT last_insert_rowid();"
    command.Parameters.AddWithValue("@s", supplierId) |> ignore
    Convert.ToInt64(command.ExecuteScalar())

let private insertInvoice (connectionString: string) (supplierId: int64) (templateId: int64) (reference: string) =
    execParams
        connectionString
        "INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, MessageReceivedAt, ScannedAt)
         VALUES (@s, @t, @r, '10.00', 'AUD', NULL, NULL, 'msg', '2026-05-20T00:00:00.0000000', '2026-06-01T00:00:00.0000000')"
        [ "@s", box supplierId; "@t", box templateId; "@r", box reference ]

// ============================ 6.1 Invoices ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates Invoices with its expected columns`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        Assert.Contains("Invoices", tableNames cs)

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "TemplateId"; "Reference"; "Amount"; "Currency"; "IssueDate"; "DueDate"; "SourceMessageId"; "MessageReceivedAt"; "ScannedAt" ],
            columnNames cs "Invoices"
        ))

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on (SupplierId, Reference) refuses a duplicate`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        let s = insertSupplier cs
        let t = insertTemplate cs s
        insertInvoice cs s t "INV-1"

        Assert.Contains("IX_Invoices_SupplierId_Reference", indexNames cs "Invoices")

        let ex = Assert.Throws<SqliteException>(fun () -> insertInvoice cs s t "INV-1")
        Assert.Contains("UNIQUE", ex.Message))

[<Fact; Trait("Level", "Integration")>]
let ``the Invoices supplier foreign key rejects an unknown supplier`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        let s = insertSupplier cs
        let t = insertTemplate cs s
        Assert.Throws<SqliteException>(fun () -> insertInvoice cs 999L t "INV-X") |> ignore)

/// TemplateId is PROVENANCE, not a relationship - requirements.md asks only that an invoice
/// "record which template produced it", and nothing joins the two: InvoiceRecordMappers reads the
/// column straight back into an opaque TemplateId and InvoiceUiType does not carry one at all.
/// So the column deliberately has no foreign key, exactly as ScanProblems.SupplierId does and for
/// the same stated reason ("the supplier may legitimately be gone ... a diagnostic, not a
/// relationship"). An unknown TemplateId is therefore accepted.
[<Fact; Trait("Level", "Integration")>]
let ``the Invoices table accepts a TemplateId whose template is gone`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        let s = insertSupplier cs
        insertInvoice cs s 999L "INV-Y"
        Assert.Equal(1L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM Invoices")))

/// An invoice is a stored fact (Q5.7) and the ledger is what this change exists to keep. Deleting
/// the TEMPLATE that produced one must not take the invoice with it: a template is a parsing
/// recipe, the invoice is the result, and invoice-templates requirements.md line 167 says deleting
/// a template deletes "it and its rules" - not the ledger rows it once produced. There is no
/// tombstone for a cascade, so the rows would be gone silently and permanently.
[<Fact; Trait("Level", "Integration")>]
let ``deleting a template leaves the invoices it produced`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        let s = insertSupplier cs
        let keptTemplate = insertTemplate cs s
        let doomedTemplate = insertTemplate cs s

        insertInvoice cs s doomedTemplate "INV-FROM-DELETED-TEMPLATE"
        insertInvoice cs s keptTemplate "INV-FROM-KEPT-TEMPLATE"

        execParams cs "DELETE FROM InvoiceTemplates WHERE Id = @t" [ "@t", box doomedTemplate ]

        Assert.Equal(1L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM InvoiceTemplates"))
        Assert.Equal(2L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM Invoices"))

        let references =
            use connection = new SqliteConnection(cs)
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
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        let s = insertSupplier cs
        let t = insertTemplate cs s
        insertInvoice cs s t "INV-1"

        execParams cs "DELETE FROM Suppliers WHERE Id = @s" [ "@s", box s ]

        Assert.Equal(0L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM Invoices")))

[<Fact; Trait("Level", "Integration")>]
let ``Down on every change #4 migration removes all five tables, and MigrateUp rebuilds them`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        MigrationSetup.rollbackAll cs

        for table in [ "Invoices"; "ScanProblems"; "InvoiceTombstones"; "ScanWindows"; "InvoiceSettings" ] do
            Assert.DoesNotContain(table, tableNames cs)

        MigrationSetup.setupMigrations cs

        for table in [ "Invoices"; "ScanProblems"; "InvoiceTombstones"; "ScanWindows"; "InvoiceSettings" ] do
            Assert.Contains(table, tableNames cs)

        // the seed comes back on a genuine rebuild (VersionInfo was cleared by rollbackAll)
        Assert.Equal(5L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM ScanWindows")))

// ============================ 6.2 ScanProblems ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates ScanProblems with its columns and the SourceMessageId index`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs

        Assert.Equal<string list>(
            [ "Id"; "SourceMessageId"; "SupplierId"; "Sender"; "Subject"; "ReceivedAt"; "Cause"; "Detail"; "RecordedAt" ],
            columnNames cs "ScanProblems"
        )

        Assert.Contains("IX_ScanProblems_SourceMessageId", indexNames cs "ScanProblems"))

// ============================ 6.3 InvoiceTombstones ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates InvoiceTombstones with its columns and a unique (SupplierId, Reference) index`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "Reference"; "DeletedAt" ],
            columnNames cs "InvoiceTombstones"
        )

        Assert.Contains("IX_InvoiceTombstones_SupplierId_Reference", indexNames cs "InvoiceTombstones")

        let s = insertSupplier cs

        let insertTombstone () =
            execParams
                cs
                "INSERT INTO InvoiceTombstones (SupplierId, Reference, DeletedAt) VALUES (@s, 'INV-1', '2026-06-01T00:00:00.0000000')"
                [ "@s", box s ]

        insertTombstone ()
        Assert.Throws<SqliteException>(insertTombstone) |> ignore)

// ============================ 6.4 ScanWindows (+ seed) ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp seeds exactly the five starting windows`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        Assert.Equal(5L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM ScanWindows"))

        let days =
            use connection = new SqliteConnection(cs)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT Days FROM ScanWindows ORDER BY Days"
            use reader = command.ExecuteReader()
            [ while reader.Read() do yield reader.GetInt32 0 ]

        Assert.Equal<int list>([ 7; 14; 30; 90; 180 ], days))

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on Days refuses a sixth 14`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        Assert.Contains("IX_ScanWindows_Days", indexNames cs "ScanWindows")
        Assert.Throws<SqliteException>(fun () -> exec cs "INSERT INTO ScanWindows (Days) VALUES (14)") |> ignore)

[<Fact; Trait("Level", "Integration")>]
let ``the ScanWindows Down runs its Delete.FromTable and drops the table cleanly`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        // rollbackTo the version just before ScanWindows: its Down (Delete.FromTable x5, then
        // Delete.Index, then Delete.Table) must run without error - a Delete.FromTable against a
        // row that is not there would still succeed, but a malformed one would throw here.
        MigrationSetup.rollbackToVersion cs 20260810000006L
        Assert.DoesNotContain("ScanWindows", tableNames cs)
        Assert.Contains("InvoiceTombstones", tableNames cs))

[<Fact; Trait("Level", "Integration")>]
let ``re-running migrations after a user deletes a seeded window does not restore it`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        exec cs "DELETE FROM ScanWindows WHERE Days = 30"
        Assert.Equal(4L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM ScanWindows"))

        MigrationSetup.setupMigrations cs // already applied - the seed does not re-run
        Assert.Equal(4L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM ScanWindows"))
        Assert.Equal(0L, Convert.ToInt64(queryScalar cs "SELECT COUNT(*) FROM ScanWindows WHERE Days = 30")))

// ============================ 6.5 InvoiceSettings ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates InvoiceSettings fixed at a single row`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs

        Assert.Equal<string list>([ "Id"; "SelectedScanWindowDays" ], columnNames cs "InvoiceSettings")

        exec cs "INSERT INTO InvoiceSettings (Id, SelectedScanWindowDays) VALUES (1, NULL)"
        // a second row - whether Id 1 (primary key) or Id 2 (check) - is refused
        Assert.Throws<SqliteException>(fun () -> exec cs "INSERT INTO InvoiceSettings (Id, SelectedScanWindowDays) VALUES (1, 14)") |> ignore
        Assert.Throws<SqliteException>(fun () -> exec cs "INSERT INTO InvoiceSettings (Id, SelectedScanWindowDays) VALUES (2, 14)") |> ignore)

[<Fact; Trait("Level", "Integration")>]
let ``the InvoiceSettings setting column is nullable`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        exec cs "INSERT INTO InvoiceSettings (Id) VALUES (1)"
        Assert.Equal(DBNull.Value :> obj, queryScalar cs "SELECT SelectedScanWindowDays FROM InvoiceSettings WHERE Id = 1"))

[<Fact; Trait("Level", "Integration")>]
let ``Down reverses the InvoiceSettings migration`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        MigrationSetup.rollbackToVersion cs 20260810000007L
        Assert.DoesNotContain("InvoiceSettings", tableNames cs)
        Assert.Contains("ScanWindows", tableNames cs))
