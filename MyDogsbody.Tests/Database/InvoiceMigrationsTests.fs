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
        "INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, ScannedAt)
         VALUES (@s, @t, @r, '10.00', 'AUD', NULL, NULL, 'msg', '2026-06-01T00:00:00.0000000')"
        [ "@s", box supplierId; "@t", box templateId; "@r", box reference ]

// ============================ 6.1 Invoices ============================

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates Invoices with its expected columns`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        Assert.Contains("Invoices", tableNames cs)

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "TemplateId"; "Reference"; "Amount"; "Currency"; "IssueDate"; "DueDate"; "SourceMessageId"; "ScannedAt" ],
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
let ``the Invoices foreign keys reject an unknown supplier or template`` () =
    withTempDatabase (fun cs ->
        MigrationSetup.setupMigrations cs
        let s = insertSupplier cs
        let t = insertTemplate cs s
        Assert.Throws<SqliteException>(fun () -> insertInvoice cs 999L t "INV-X") |> ignore
        Assert.Throws<SqliteException>(fun () -> insertInvoice cs s 999L "INV-Y") |> ignore)

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
