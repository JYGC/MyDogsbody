module MyDogsbody.Tests.Database.DatabaseContextSetupTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Database
open MyDogsbody.Database.Migrations

let private withTempPath (test: string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")

    try
        test databaseFilePath
    finally
        SqliteConnection.ClearAllPools()
        try File.Delete databaseFilePath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``a context created against a temp file can be disposed and the file then deletes successfully`` () =
    withTempPath (fun databaseFilePath ->
        MigrationSetup.setupMigrations $"Data Source={databaseFilePath}"
        let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

        // touch the connection so there is something to release
        use connection = context.GetDatabaseConnection()
        connection.Open()
        connection.Close()

        context.Dispose()

        // Microsoft.Data.Sqlite pools connections, so a pooled handle can keep the file locked
        // on Windows even after Dispose() - the pool must be cleared first.
        SqliteConnection.ClearAllPools()

        // no try/with here on purpose: the delete must actually succeed
        File.Delete databaseFilePath
        Assert.False(File.Exists databaseFilePath)
    )

[<Fact; Trait("Level", "Integration")>]
let ``PRAGMA foreign_keys reads back as 1 on the connection handed out by GetDatabaseConnection`` () =
    withTempPath (fun databaseFilePath ->
        MigrationSetup.setupMigrations $"Data Source={databaseFilePath}"
        let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

        try
            let connection = context.GetDatabaseConnection()
            connection.Open()

            use command = connection.CreateCommand()
            command.CommandText <- "PRAGMA foreign_keys"
            let actual = command.ExecuteScalar()

            Assert.Equal(1L, Convert.ToInt64 actual)
        finally
            context.Dispose()
    )
