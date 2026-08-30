module MyDogsbody.Tests.Contracts.GoogleCredentialPersistedShapeTests

open System
open System.IO
open Xunit
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database

// LiteDB is schemaless, so renaming a property on GoogleCredential silently orphans every row
// already stored - the code keeps compiling and the old data simply stops being found. This
// asserts the persisted document's field names, not just that an object round trips.
//
// It also nails down the two things this whole change is about: the collection is named
// Credentials (the established name, carried across), and there is NO discriminator field - the
// database is the provider.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private withStoreAndRawAccess
    (test: (unit -> Database.Types.GoogleCredentialsCollection) -> LiteDatabase -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "shared"
    use rawDatabase = new LiteDatabase($"Filename={databasePath};connection=shared")

    try
        test context.GetCredentialCollection rawDatabase
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

let private aCredential secret username : ValidGoogleCredential =
    {
        Secret = GoogleCredentialSecret.create secret |> valueOrFail
        Username = GoogleExternalUsername.create username |> valueOrFail
    }

[<Fact; Trait("Level", "Contract")>]
let ``a Google credential is persisted under the documented field names`` () =
    withStoreAndRawAccess (fun getCollection rawDatabase ->
        // Arrange / Act
        aCredential "persisted-secret" "persisted@gmail.com"
        |> GoogleCredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Assert
        let document = rawDatabase.GetCollection("Credentials").FindAll() |> Seq.exactlyOne

        Assert.True(document.ContainsKey "_id", "expected an _id field")
        Assert.True(document.ContainsKey "Credentials", "expected a Credentials field")
        Assert.True(document.ContainsKey "ExternalUsername", "expected an ExternalUsername field")

        // The UI calls it Username; that name never reaches the store.
        Assert.False(document.ContainsKey "Username", "Username must not be persisted")

        // The whole point of the change: no discriminator. The database is the provider.
        Assert.False(document.ContainsKey "InfrastructureType", "InfrastructureType must not be persisted")
        Assert.False(document.ContainsKey "Infrastructure", "Infrastructure must not be persisted")
        Assert.False(document.ContainsKey "Provider", "Provider must not be persisted")

        Assert.Equal("persisted-secret", document.["Credentials"].AsString)
        Assert.Equal("persisted@gmail.com", document.["ExternalUsername"].AsString)
    )

[<Fact; Trait("Level", "Contract")>]
let ``the credentials collection is named Credentials and is the only collection in the database`` () =
    withStoreAndRawAccess (fun getCollection rawDatabase ->
        // Arrange / Act
        aCredential "secret" "person@gmail.com"
        |> GoogleCredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Assert
        Assert.Equal<string list>([ "Credentials" ], rawDatabase.GetCollectionNames() |> List.ofSeq)
    )

[<Fact; Trait("Level", "Contract")>]
let ``a secret is persisted with its surrounding whitespace intact`` () =
    withStoreAndRawAccess (fun getCollection rawDatabase ->
        // Arrange / Act - the shared store trimmed this; the per-provider store must not
        aCredential "  1//0-abc_DEF  " "person@gmail.com"
        |> GoogleCredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Assert - the raw document, as stored
        let document = rawDatabase.GetCollection("Credentials").FindAll() |> Seq.exactlyOne
        Assert.Equal("  1//0-abc_DEF  ", document.["Credentials"].AsString)
    )
