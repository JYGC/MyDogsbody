module MyDogsbody.Tests.Contracts.CredentialHopChainTests

open System
open System.IO
open Xunit
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Enums
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private handleError = HandleErrorBuilder (fun _ -> ())

/// Shared connection so the test can open a second handle and inspect the raw documents.
let private withApiAndRawAccess (test: CredentialApi -> LiteDatabase -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "shared"
    let api = CredentialApiFactory.createCredentialApi handleError context.GetCredentialCollection
    use rawDatabase = new LiteDatabase($"Filename={databasePath};connection=shared")
    try
        test api rawDatabase
    finally
        try File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Contract")>]
let ``a credential entered in the UI is persisted under the documented field names`` () =
    withApiAndRawAccess (fun api rawDatabase ->
        // Arrange / Act
        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Microsoft
                Credentials = "persisted-secret"
                Username = "persisted@outlook.com"
            }
        |> ignore

        let document =
            rawDatabase.GetCollection("Credentials").FindAll() |> Seq.exactlyOne

        // LiteDB is schemaless, so renaming a property on the Credential entity would
        // silently orphan stored data. Assert the field names, not just the round trip.
        Assert.True(document.ContainsKey "_id", "expected an _id field")
        Assert.True(document.ContainsKey "InfrastructureType", "expected an InfrastructureType field")
        Assert.True(document.ContainsKey "Credentials", "expected a Credentials field")
        Assert.True(document.ContainsKey "ExternalUsername", "expected an ExternalUsername field")

        // Username is deliberately renamed to ExternalUsername at the domain boundary and
        // must not reach the store under its UI name.
        Assert.False(document.ContainsKey "Username", "Username must not be persisted")

        Assert.Equal("persisted-secret", document.["Credentials"].AsString)
        Assert.Equal("persisted@outlook.com", document.["ExternalUsername"].AsString)
    )

[<Fact; Trait("Level", "Contract")>]
let ``a credential survives the whole chain from UI record to store and back`` () =
    withApiAndRawAccess (fun api _ ->
        // Arrange
        let entered: IntegrationCredentialUiTypeWithoutId =
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = """{ "refresh_token": "1//abcDEF" }"""
                Username = "chain@gmail.com"
            }

        // Act
        api.AddCredential entered |> ignore

        // Assert — every field arrives back unchanged, and only Id is added
        match api.GetAllCredentials() with
        | Ok [ readBack ] ->
            Assert.False(String.IsNullOrWhiteSpace readBack.Id)
            Assert.Equal(entered.InfrastructureType, readBack.InfrastructureType)
            Assert.Equal(entered.Credentials, readBack.Credentials)
            Assert.Equal(entered.Username, readBack.Username)
        | Ok other -> Assert.Fail($"Expected exactly one credential, got {other.Length}")
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

[<Fact; Trait("Level", "Contract")>]
let ``every infrastructure type round trips through the store`` () =
    withApiAndRawAccess (fun api _ ->
        // Arrange — guards against an enum that persists in a form it cannot be read back from
        let allTypes =
            Enum.GetValues(typeof<InfrastructureType>)
            |> Seq.cast<InfrastructureType>
            |> Seq.toList

        // Act
        allTypes
        |> List.iter (fun infrastructureType ->
            api.AddCredential
                {
                    InfrastructureType = infrastructureType
                    Credentials = $"{infrastructureType}-secret"
                    Username = $"{infrastructureType}@example.com"
                }
            |> ignore
        )

        // Assert
        match api.GetAllCredentials() with
        | Ok credentials ->
            Assert.Equal(allTypes.Length, credentials.Length)
            allTypes
            |> List.iter (fun infrastructureType ->
                let stored =
                    credentials
                    |> List.find (fun c -> c.InfrastructureType = infrastructureType)
                Assert.Equal($"{infrastructureType}-secret", stored.Credentials)
                Assert.Equal($"{infrastructureType}@example.com", stored.Username)
            )
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )
