module MyDogsbody.Tests.Integrations.Credentials.CredentialsDatabaseContextModuleTests

open System
open System.IO
open Xunit
open MyDogsbody.Enums
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Integrations.Credentials.Database.Models

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
        let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"

        try
            // Act
            let collection = context.GetCredentialCollection()

            Credential(
                InfrastructureType = InfrastructureType.Google,
                Credentials = "secret",
                ExternalUsername = "person@gmail.com"
            )
            |> collection.Insert
            |> ignore

            // Assert - the same getter reaches the same collection
            let stored = context.GetCredentialCollection().FindAll() |> Seq.exactlyOne
            Assert.Equal(InfrastructureType.Google, stored.InfrastructureType)
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

        let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"

        try
            // Act - LiteDB creates the file lazily, so touch the collection
            context.GetCredentialCollection().FindAll() |> Seq.toList |> ignore

            // Assert
            Assert.True(File.Exists databasePath, "expected the database file to have been created")
        finally
            context.Dispose()
    )

[<Fact; Trait("Level", "Integration")>]
let ``Dispose releases the file so it can be deleted`` () =
    // Arrange - Windows holds a LiteDB file open until the handle is disposed, which is why
    // integration tests used to delete their temp files on a best-effort basis.
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"
    context.GetCredentialCollection().FindAll() |> Seq.toList |> ignore

    // Act
    context.Dispose()

    // Assert - no try/with here on purpose: the delete must actually succeed
    File.Delete databasePath
    Assert.False(File.Exists databasePath)
