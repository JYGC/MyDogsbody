module MyDogsbody.Tests.Database.MigrationsTests

open System
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Tests.Database.MigrationTestHelpers

// The migrations are the schema source of truth for the main database, so a test never writes
// its own DDL - it calls setupMigrations and asserts what that produced. Nothing else verifies a
// migration before it runs against real data.

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp on an empty file creates the Blogs table with its expected columns`` () =
    withTempDatabase (fun connectionString ->
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
    withTempDatabase (fun connectionString ->
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
    withTempDatabase (fun connectionString ->
        // Act
        MigrationSetup.setupMigrations connectionString

        // Assert - every migration in the assembly, by their timestamps. Grows as new changes
        // add migrations - see docs/changes/invoice-to-calendar/background.md for the reserved
        // timestamp blocks each change owns.
        let applied = queryScalar connectionString "SELECT COUNT(*) FROM VersionInfo"
        Assert.Equal(12L, Convert.ToInt64 applied)

        let latest = queryScalar connectionString "SELECT MAX(Version) FROM VersionInfo"
        Assert.Equal(20260810000008L, Convert.ToInt64 latest)
    )

[<Fact; Trait("Level", "Integration")>]
let ``MigrateUp is idempotent when run twice against the same database`` () =
    withTempDatabase (fun connectionString ->
        // Act
        MigrationSetup.setupMigrations connectionString
        MigrationSetup.setupMigrations connectionString

        // Assert - the second run applies nothing new
        let applied = queryScalar connectionString "SELECT COUNT(*) FROM VersionInfo"
        Assert.Equal(12L, Convert.ToInt64 applied)
    )

[<Fact; Trait("Level", "Integration")>]
let ``the migrated schema accepts a row in each table`` () =
    withTempDatabase (fun connectionString ->
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
    withTempDatabase (fun connectionString ->
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
    withTempDatabase (fun connectionString ->
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

        Assert.Equal(12L, Convert.ToInt64(queryScalar connectionString "SELECT COUNT(*) FROM VersionInfo"))
    )

[<Fact; Trait("Level", "Integration")>]
let ``createDatabaseContext opens a connection against a migrated database`` () =
    withTempDatabaseAndPath (fun databaseFilePath connectionString ->
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
