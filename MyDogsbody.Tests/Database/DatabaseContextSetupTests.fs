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
        try File.Delete databaseFilePath with _ -> ()

/// The migration runner's connection string, with pooling off to match DatabaseContextSetup.fs -
/// so a FluentMigrator connection does not linger in a pool holding the temp file.
let private migrationConnectionString databaseFilePath =
    $"Data Source={databaseFilePath};Pooling=False"

[<Fact; Trait("Level", "Integration")>]
let ``createDatabaseContext opens with pooling disabled and foreign keys on`` () =
    withTempPath (fun databaseFilePath ->
        MigrationSetup.setupMigrations (migrationConnectionString databaseFilePath)
        let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

        try
            let builder = SqliteConnectionStringBuilder(context.GetDatabaseConnection().ConnectionString)
            // Pooling=False is what lets context.Dispose() release the file handle immediately,
            // instead of a pooled connection keeping the temp file locked and the harnesses reaching
            // for the process-global pool clear (the ClearAllPools hammer) - see docs/changes/sqlite-pool-flake.
            Assert.False(builder.Pooling)
            Assert.Equal(System.Nullable true, builder.ForeignKeys)
        finally
            context.Dispose()
    )

[<Fact; Trait("Level", "Integration")>]
let ``a context created against a temp file can be disposed and the file then deletes successfully`` () =
    withTempPath (fun databaseFilePath ->
        MigrationSetup.setupMigrations (migrationConnectionString databaseFilePath)
        let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

        // touch the connection so there is something to release
        use connection = context.GetDatabaseConnection()
        connection.Open()
        connection.Close()

        context.Dispose()

        // Pooling is disabled on both connection strings (the migration runner's above and the
        // one createDatabaseContext builds - see the test above), so Dispose() and the `use`
        // block close their handles rather than returning them to a pool that would keep the file
        // locked on Windows. No process-global pool clear (the ClearAllPools hammer), which would
        // clear pooled connections other parallel tests are mid-use of.
        //
        // no try/with here on purpose: the delete must actually succeed
        File.Delete databaseFilePath
        Assert.False(File.Exists databaseFilePath)
    )

[<Fact; Trait("Level", "Integration")>]
let ``PRAGMA foreign_keys reads back as 1 on the connection handed out by GetDatabaseConnection`` () =
    withTempPath (fun databaseFilePath ->
        MigrationSetup.setupMigrations (migrationConnectionString databaseFilePath)
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
