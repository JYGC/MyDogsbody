module MyDogsbody.Tests.Database.MigrationsTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Database
open MyDogsbody.Database.Migrations

// The migrations are the schema source of truth for the main database, so a test never writes
// its own DDL - it calls setupMigrations and asserts what that produced. Nothing else verifies a
// migration before it runs against real data.

/// Fresh temp database per test. Microsoft.Data.Sqlite pools connections, so a pooled handle can
/// keep the file locked on Windows - the pool is cleared before the delete.
let private withTempDatabase (test: string -> string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath}"

    try
        test databaseFilePath connectionString
    finally
        SqliteConnection.ClearAllPools()
        try File.Delete databaseFilePath with _ -> ()

let private queryScalar (connectionString: string) (sql: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteScalar()

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
let ``MigrateUp on an empty file creates the Blogs table with its expected columns`` () =
    withTempDatabase (fun _ connectionString ->
        // Act
        MigrationSetup.setupMigrations connectionString

        // Assert
        Assert.Contains("Blogs", tableNames connectionString)

        Assert.Equal<string list>(
            [ "Id"; "Title"; "Content"; "CreatedAt" ],
            columnNames connectionString "Blogs"
        )
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp on an empty file creates the Comments table with its expected columns`` () =
    withTempDatabase (fun _ connectionString ->
        // Act
        MigrationSetup.setupMigrations connectionString

        // Assert
        Assert.Contains("Comments", tableNames connectionString)

        Assert.Equal<string list>(
            [ "Id"; "BlogId"; "Author"; "Content"; "CreatedAt" ],
            columnNames connectionString "Comments"
        )
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp records the applied migrations in the version table`` () =
    withTempDatabase (fun _ connectionString ->
        // Act
        MigrationSetup.setupMigrations connectionString

        // Assert - every migration in the assembly, by their timestamps. Grows as new changes
        // add migrations - see docs/changes/invoice-to-calendar/background.md for the reserved
        // timestamp blocks each change owns.
        let applied = queryScalar connectionString "SELECT COUNT(*) FROM VersionInfo"
        Assert.Equal(4L, Convert.ToInt64 applied)

        let latest = queryScalar connectionString "SELECT MAX(Version) FROM VersionInfo"
        Assert.Equal(20260809000002L, Convert.ToInt64 latest)
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp is idempotent when run twice against the same database`` () =
    withTempDatabase (fun _ connectionString ->
        // Act
        MigrationSetup.setupMigrations connectionString
        MigrationSetup.setupMigrations connectionString

        // Assert - the second run applies nothing new
        let applied = queryScalar connectionString "SELECT COUNT(*) FROM VersionInfo"
        Assert.Equal(4L, Convert.ToInt64 applied)
    )

[<Fact; Trait("Level", "Integration")>]
let ``the migrated schema accepts a row in each table`` () =
    withTempDatabase (fun _ connectionString ->
        // Arrange
        MigrationSetup.setupMigrations connectionString

        // Act
        use connection = new SqliteConnection(connectionString)
        connection.Open()
        use command = connection.CreateCommand()

        command.CommandText <-
            "INSERT INTO Blogs (Title, Content) VALUES ('a title', 'some content');
             INSERT INTO Comments (BlogId, Author, Content)
             VALUES (last_insert_rowid(), 'an author', 'a comment');"

        command.ExecuteNonQuery() |> ignore

        // Assert
        Assert.Equal(1L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM Blogs"))
        Assert.Equal(1L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM Comments"))
    )

[<Fact; Trait("Level", "Integration")>]
let ``Down reverses the migrations, leaving no domain tables behind`` () =
    withTempDatabase (fun _ connectionString ->
        // Arrange
        MigrationSetup.setupMigrations connectionString
        Assert.Contains("Blogs", tableNames connectionString)

        // Act
        MigrationSetup.rollbackAll connectionString

        // Assert - both Down() methods ran, and rolling back to version 0 takes FluentMigrator's
        // own VersionInfo table with it, leaving the file as it was found
        let remaining = tableNames connectionString
        Assert.DoesNotContain("Blogs", remaining)
        Assert.DoesNotContain("Comments", remaining)
        Assert.DoesNotContain("VersionInfo", remaining)
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp after a full rollback rebuilds the schema`` () =
    withTempDatabase (fun _ connectionString ->
        // Arrange - proves Down() left nothing behind that would block a re-apply
        MigrationSetup.setupMigrations connectionString
        MigrationSetup.rollbackAll connectionString

        // Act
        MigrationSetup.setupMigrations connectionString

        // Assert
        Assert.Equal<string list>(
            [ "Id"; "Title"; "Content"; "CreatedAt" ],
            columnNames connectionString "Blogs"
        )

        Assert.Equal(4L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM VersionInfo"))
    )

[<Fact; Trait("Level", "Integration")>]
let ``createDatabaseContext opens a connection against a migrated database`` () =
    withTempDatabase (fun databaseFilePath connectionString ->
        // Arrange
        MigrationSetup.setupMigrations connectionString

        // Act
        let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

        // Assert - createDatabaseContext never disposes what it opens, so the test does
        use connection = context.GetDatabaseConnection()
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT COUNT(*) FROM Blogs"
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()))
    )
