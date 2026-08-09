module MyDogsbody.Tests.Database.SupplierMigrationsTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Database.Migrations

// The migrations are the schema source of truth for the main database, so a test never writes
// its own DDL - it calls setupMigrations and asserts what that produced.

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

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates the Suppliers table with its expected columns`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Contains("Suppliers", tableNames connectionString)

        Assert.Equal<string list>(
            [ "Id"; "Name"; "PaymentTermDays" ],
            columnNames connectionString "Suppliers"
        )
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp creates the SupplierMatchers table with its expected columns`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        Assert.Contains("SupplierMatchers", tableNames connectionString)

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "Kind"; "Value" ],
            columnNames connectionString "SupplierMatchers"
        )
    )

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on Suppliers Name refuses a second row with the same name`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        exec connectionString "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30);"

        let duplicate () =
            exec connectionString "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 14);"

        Assert.Throws<SqliteException>(duplicate) |> ignore
    )

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on Suppliers Name refuses a second row differing only by case`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        exec connectionString "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30);"

        let duplicate () =
            exec connectionString "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('acme', 14);"

        Assert.Throws<SqliteException>(duplicate) |> ignore
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleting a supplier removes its matchers when foreign keys are enforced`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString

        exec
            connectionString
            "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30);
             INSERT INTO SupplierMatchers (SupplierId, Kind, Value)
             VALUES (last_insert_rowid(), 'Domain', 'acme.example');"

        Assert.Equal(1L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM SupplierMatchers"))

        exec connectionString "DELETE FROM Suppliers WHERE Name = 'Acme';"

        Assert.Equal(0L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM SupplierMatchers"))
    )

[<Fact; Trait("Level", "Integration")>]
let ``Down on both supplier migrations removes both tables and their index`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        Assert.Contains("Suppliers", tableNames connectionString)
        Assert.Contains("SupplierMatchers", tableNames connectionString)

        MigrationSetup.rollbackAll connectionString

        let remaining = tableNames connectionString
        Assert.DoesNotContain("Suppliers", remaining)
        Assert.DoesNotContain("SupplierMatchers", remaining)
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp after a full rollback rebuilds the supplier schema`` () =
    withTempDatabase (fun connectionString ->
        MigrationSetup.setupMigrations connectionString
        MigrationSetup.rollbackAll connectionString

        MigrationSetup.setupMigrations connectionString

        Assert.Equal<string list>(
            [ "Id"; "Name"; "PaymentTermDays" ],
            columnNames connectionString "Suppliers"
        )

        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "Kind"; "Value" ],
            columnNames connectionString "SupplierMatchers"
        )
    )
