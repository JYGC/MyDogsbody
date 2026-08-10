module MyDogsbody.Tests.Database.InvoiceTemplateMigrationsTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Database.Migrations

// The migrations are the schema source of truth for the main database, so a test never writes
// its own DDL - it calls setupMigrations and asserts what that produced. Both tables here carry a
// foreign key, so both Up() methods are Execute.Sql rather than the fluent Create.Table() builder -
// see design.md.

let private withTempDatabase (test: string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath}"

    try
        test connectionString
    finally
        SqliteConnection.ClearAllPools()
        try File.Delete databaseFilePath with _ -> ()

let private queryScalar (connectionString: string) (sql: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteScalar()

let private exec (connectionString: string) (sql: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "PRAGMA foreign_keys = ON;" + sql
    command.ExecuteNonQuery() |> ignore

let private tableNames (connectionString: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"
    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            yield reader.GetString 0
    ]

let private indexNames (connectionString: string) (tableName: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- $"SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = '{tableName}'"
    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            yield reader.GetString 0
    ]

let private columnNames (connectionString: string) (tableName: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{tableName}')"
    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            yield reader.GetString 1
    ]

// INSERT and its SELECT last_insert_rowid() run as one command on one connection - last_insert_rowid()
// is scoped to the connection that performed the insert, so splitting them across separate
// queryScalar/exec calls (each opening its own SqliteConnection) reads whatever the pool happens to
// hand back rather than the row just inserted. That is silently correct when nothing else is
// running concurrently and silently wrong under the full suite's parallelism - exactly the failure
// mode this comment exists to rule out.
let private insertSupplierReturningId (connectionString: string) (name: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        $"PRAGMA foreign_keys = ON;
          INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('{name}', 30);
          SELECT last_insert_rowid();"
    Convert.ToInt64(command.ExecuteScalar())

let private insertTemplateReturningId (connectionString: string) (supplierId: int64) (position: int) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        $"PRAGMA foreign_keys = ON;
          INSERT INTO InvoiceTemplates (SupplierId, Name, DocumentPart, AttachmentFormat, Position)
          VALUES ({supplierId}, 'Template', 'AnyPart', NULL, {position});
          SELECT last_insert_rowid();"
    Convert.ToInt64(command.ExecuteScalar())

let private insertFieldRule (connectionString: string) (templateId: int64) (targetField: string) =
    exec
        connectionString
        $"INSERT INTO TemplateFieldRules
            (TemplateId, TargetField, RuleKind, RuleText, RuleOffset, RuleSourceField, HintKind, HintText)
          VALUES ({templateId}, '{targetField}', 'AfterLabel', 'Total:', NULL, NULL, 'AsText', NULL);"

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates the InvoiceTemplates table with its expected columns`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Contains("InvoiceTemplates", tableNames connectionString)

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "Name"; "DocumentPart"; "AttachmentFormat"; "Position" ],
            columnNames connectionString "InvoiceTemplates"
        )
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

        Assert.Equal<string list>(
            [ "Id"; "TemplateId"; "TargetField"; "RuleKind"; "RuleText"; "RuleOffset"; "RuleSourceField"; "HintKind"; "HintText" ],
            columnNames connectionString "TemplateFieldRules"
        )
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

        exec connectionString $"DELETE FROM InvoiceTemplates WHERE Id = {templateId};"

        Assert.Equal(0L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM TemplateFieldRules"))
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleting a supplier removes its templates and their field rules when foreign keys are enforced`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        let supplierId = insertSupplierReturningId connectionString "Acme"
        let templateId = insertTemplateReturningId connectionString supplierId 0
        insertFieldRule connectionString templateId "Reference"

        exec connectionString $"DELETE FROM Suppliers WHERE Id = {supplierId};"

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

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "Name"; "DocumentPart"; "AttachmentFormat"; "Position" ],
            columnNames connectionString "InvoiceTemplates"
        )

        Assert.Equal<string list>(
            [ "Id"; "TemplateId"; "TargetField"; "RuleKind"; "RuleText"; "RuleOffset"; "RuleSourceField"; "HintKind"; "HintText" ],
            columnNames connectionString "TemplateFieldRules"
        )
    )
