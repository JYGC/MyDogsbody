/// Shared connection/query helpers for migration tests run against a fresh temp SQLite file. Used
/// by all three migration test files - MigrationsTests.fs, SupplierMigrationsTests.fs and
/// InvoiceTemplateMigrationsTests.fs - which each used to carry their own independent copy of this
/// boilerplate.
module MyDogsbody.Tests.Database.MigrationTestHelpers

open System
open System.IO
open Microsoft.Data.Sqlite

/// Fresh temp database per test. `Foreign Keys=True` on the connection string matches
/// DatabaseContextSetup.fs - PRAGMAs are per-connection and off by default, so this is what makes
/// every connection opened against this file self-apply FK enforcement, rather than every write
/// helper having to remember to prepend the PRAGMA by hand. Microsoft.Data.Sqlite pools
/// connections, so a pooled handle can keep the file locked on Windows - the pool is cleared
/// before the delete.
///
/// Hands back the file path as well as the connection string - DatabaseContextSetup.createDatabaseContext
/// takes a path, not a connection string, so the one caller that exercises it needs both.
/// `withTempDatabase` below is the same setup/teardown for the (more common) callers that only
/// need the connection string.
let withTempDatabaseAndPath (test: string -> string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Foreign Keys=True"

    try
        test databaseFilePath connectionString
    finally
        SqliteConnection.ClearAllPools()
        try File.Delete databaseFilePath with _ -> ()

let withTempDatabase (test: string -> unit) =
    withTempDatabaseAndPath (fun _ connectionString -> test connectionString)

let queryScalar (connectionString: string) (sql: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteScalar()

let exec (connectionString: string) (sql: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

/// Parameterised sibling of `exec`, for statements that carry a value rather than only literals a
/// test wrote itself - e.g. deleting a row by an id computed earlier in the same test.
let execParams (connectionString: string) (sql: string) (parameters: (string * obj) list) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    for (name, value) in parameters do
        command.Parameters.AddWithValue(name, value) |> ignore
    command.ExecuteNonQuery() |> ignore

let tableNames (connectionString: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"
    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            yield reader.GetString 0
    ]

let indexNames (connectionString: string) (tableName: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = @tableName"
    command.Parameters.AddWithValue("@tableName", tableName) |> ignore
    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            yield reader.GetString 0
    ]

/// PRAGMA statements don't accept bound parameters in SQLite, so tableName stays interpolated
/// here - unlike indexNames' plain SELECT, there is no parameterised form to use instead. Every
/// caller passes a literal the test itself wrote, never external input.
let columnNames (connectionString: string) (tableName: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{tableName}')"
    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            yield reader.GetString 1
    ]
