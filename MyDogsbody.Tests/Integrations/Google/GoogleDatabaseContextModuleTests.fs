module MyDogsbody.Tests.Integrations.Google.GoogleDatabaseContextModuleTests

open System
open System.IO
open Xunit
open LiteDB
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Integrations.Google.Database.Models

let private withTempPath (test: string -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")

    try
        test databasePath
    finally
        try File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``getDatabaseContext returns a collection getter that can be written to and read from`` () =
    withTempPath (fun databasePath ->
        // Arrange
        let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

        try
            // Act
            let collection = context.GetCredentialCollection()

            GoogleCredential(Credentials = "secret", ExternalUsername = "person@gmail.com")
            |> collection.Insert
            |> ignore

            // Assert - the same getter reaches the same collection
            let stored = context.GetCredentialCollection().FindAll() |> Seq.exactlyOne
            Assert.Equal("secret", stored.Credentials)
            Assert.Equal("person@gmail.com", stored.ExternalUsername)
        finally
            context.Dispose()
    )

[<Fact; Trait("Level", "Integration")>]
let ``getDatabaseContext creates the database file when it does not yet exist`` () =
    withTempPath (fun databasePath ->
        // Arrange
        Assert.False(File.Exists databasePath, "the test must start with no database file")

        let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

        try
            // Act - LiteDB creates the file lazily, so touch the collection
            context.GetCredentialCollection().FindAll() |> Seq.toList |> ignore

            // Assert
            Assert.True(File.Exists databasePath, "expected the database file to have been created")
        finally
            context.Dispose()
    )

[<Fact; Trait("Level", "Integration")>]
let ``the local mapper preserves leading and trailing whitespace on a string property`` () =
    withTempPath (fun databasePath ->
        // Arrange - this is the reason the context uses a local BsonMapper rather than the global
        // one: BsonMapper.Global.TrimWhitespace defaults to true and would silently trim a secret
        let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

        try
            let collection = context.GetCredentialCollection()
            GoogleCredential(Credentials = "  padded  ", ExternalUsername = "  spaced  ")
            |> collection.Insert
            |> ignore

            // Act
            let stored = context.GetCredentialCollection().FindAll() |> Seq.exactlyOne

            // Assert
            Assert.Equal("  padded  ", stored.Credentials)
            Assert.Equal("  spaced  ", stored.ExternalUsername)
        finally
            context.Dispose()
    )

[<Fact; Trait("Level", "Integration")>]
let ``Dispose releases the file so it can be deleted`` () =
    // Arrange - Windows holds a LiteDB file open until the handle is disposed
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    context.GetCredentialCollection().FindAll() |> Seq.toList |> ignore

    // Act
    context.Dispose()

    // Assert - no try/with here on purpose: the delete must actually succeed
    File.Delete databasePath
    Assert.False(File.Exists databasePath)

[<Fact; Trait("Level", "Integration")>]
let ``a fresh context maps every property of a fully populated entity - the warm-up is complete before it returns`` () =
    withTempPath (fun databasePath ->
        // Arrange - a half-built mapping round-trips a row with a null where a value was written.
        // The warm-up in getDatabaseContext builds the mapping on one thread before the context
        // is handed out, so the very first insert already sees every property.
        let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

        try
            let collection = context.GetCredentialCollection()
            GoogleCredential(Credentials = "the-secret", ExternalUsername = "the-user@gmail.com")
            |> collection.Insert
            |> ignore

            // Assert
            let stored = collection.FindAll() |> Seq.exactlyOne
            Assert.Equal("the-secret", stored.Credentials)
            Assert.Equal("the-user@gmail.com", stored.ExternalUsername)
            Assert.NotEqual(ObjectId.Empty, stored.Id)
        finally
            context.Dispose()
    )
