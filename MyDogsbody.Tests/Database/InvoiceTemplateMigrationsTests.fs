module MyDogsbody.Tests.Database.InvoiceTemplateMigrationsTests

open System
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Database.Migrations
open MyDogsbody.Tests.Database.MigrationTestHelpers

// The migrations are the schema source of truth for the main database, so a test never writes
// its own DDL - it calls setupMigrations and asserts what that produced. Both tables here carry a
// foreign key, so both Up() methods are Execute.Sql rather than the fluent Create.Table() builder -
// see design.md.

let private expectedInvoiceTemplateColumns = [ "Id"; "SupplierId"; "Name"; "DocumentPart"; "AttachmentFormat"; "Position" ]

let private expectedTemplateFieldRuleColumns =
    [ "Id"; "TemplateId"; "TargetField"; "RuleKind"; "RuleText"; "RuleOffset"; "RuleSourceField"; "HintKind"; "HintText" ]

// INSERT and its SELECT last_insert_rowid() run as one command on one connection - last_insert_rowid()
// is scoped to the connection that performed the insert, so splitting them across separate
// queryScalar/exec calls (each opening its own SqliteConnection) reads whatever the pool happens to
// hand back rather than the row just inserted. That is silently correct when nothing else is
// running concurrently and silently wrong under the full suite's parallelism - exactly the failure
// mode this comment exists to rule out. Values are bound parameters rather than spliced into the
// SQL text: currently every caller passes a hardcoded literal, but this is the shape a later
// insert helper elsewhere could copy for a value that does originate from user input.
let private insertSupplierReturningId (connectionString: string) (name: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES (@name, 30); SELECT last_insert_rowid();"
    command.Parameters.AddWithValue("@name", name) |> ignore
    Convert.ToInt64(command.ExecuteScalar())

let private insertTemplateReturningId (connectionString: string) (supplierId: int64) (position: int) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        "INSERT INTO InvoiceTemplates (SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (@supplierId, 'Template', 'AnyPart', NULL, @position);
         SELECT last_insert_rowid();"
    command.Parameters.AddWithValue("@supplierId", supplierId) |> ignore
    command.Parameters.AddWithValue("@position", position) |> ignore
    Convert.ToInt64(command.ExecuteScalar())

let private insertFieldRule (connectionString: string) (templateId: int64) (targetField: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        "INSERT INTO TemplateFieldRules
            (TemplateId, TargetField, RuleKind, RuleText, RuleOffset, RuleSourceField, HintKind, HintText)
         VALUES (@templateId, @targetField, 'AfterLabel', 'Total:', NULL, NULL, 'AsText', NULL);"
    command.Parameters.AddWithValue("@templateId", templateId) |> ignore
    command.Parameters.AddWithValue("@targetField", targetField) |> ignore
    command.ExecuteNonQuery() |> ignore

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates the InvoiceTemplates table with its expected columns`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Contains("InvoiceTemplates", tableNames connectionString)
        Assert.Equal<string list>(expectedInvoiceTemplateColumns, columnNames connectionString "InvoiceTemplates")
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates the SupplierId Position index on InvoiceTemplates`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Contains("IX_InvoiceTemplates_SupplierId_Position", indexNames connectionString "InvoiceTemplates")
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates the TemplateFieldRules table with its expected columns`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Contains("TemplateFieldRules", tableNames connectionString)
        Assert.Equal<string list>(expectedTemplateFieldRuleColumns, columnNames connectionString "TemplateFieldRules")
    )

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on TemplateId TargetField refuses a second rule for the same field`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplierReturningId connectionString "Acme"
        let templateId = insertTemplateReturningId connectionString supplierId 0
        insertFieldRule connectionString templateId "Reference"

        let duplicate () = insertFieldRule connectionString templateId "Reference"

        Assert.Throws<SqliteException>(duplicate) |> ignore
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleting a template removes its field rules when foreign keys are enforced`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplierReturningId connectionString "Acme"
        let templateId = insertTemplateReturningId connectionString supplierId 0
        insertFieldRule connectionString templateId "Reference"

        Assert.Equal(1L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM TemplateFieldRules"))

        execParams connectionString "DELETE FROM InvoiceTemplates WHERE Id = @templateId;" [ "@templateId", box templateId ]

        Assert.Equal(0L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM TemplateFieldRules"))
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleting a supplier removes its templates and their field rules when foreign keys are enforced`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplierReturningId connectionString "Acme"
        let templateId = insertTemplateReturningId connectionString supplierId 0
        insertFieldRule connectionString templateId "Reference"

        execParams connectionString "DELETE FROM Suppliers WHERE Id = @supplierId;" [ "@supplierId", box supplierId ]

        Assert.Equal(0L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM InvoiceTemplates"))
        Assert.Equal(0L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM TemplateFieldRules"))
    )

[<Fact; Trait("Level", "Integration")>]
let ``Down on both template migrations removes both tables and their indexes`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        Assert.Contains("InvoiceTemplates", tableNames connectionString)
        Assert.Contains("TemplateFieldRules", tableNames connectionString)

        MigrationSetup.rollbackAll connectionString

        let remaining = tableNames connectionString
        Assert.DoesNotContain("InvoiceTemplates", remaining)
        Assert.DoesNotContain("TemplateFieldRules", remaining)
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp after a full rollback rebuilds the template schema`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        MigrationSetup.rollbackAll connectionString

        MigrationSetup.setupMigrations connectionString

        Assert.Equal<string list>(expectedInvoiceTemplateColumns, columnNames connectionString "InvoiceTemplates")
        Assert.Equal<string list>(expectedTemplateFieldRuleColumns, columnNames connectionString "TemplateFieldRules")
    )
