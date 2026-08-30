module MyDogsbody.Tests.Integrations.Google.GoogleCredentialStoreTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database

/// No-op logger, so these tests never reach Logging.db.
let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private credential secret username : ValidGoogleCredential =
    {
        Secret = GoogleCredentialSecret.create secret |> valueOrFail
        Username = GoogleExternalUsername.create username |> valueOrFail
    }

let private edit id secret username : ValidGoogleCredentialEdit =
    {
        Id = GoogleCredentialId.create id |> valueOrFail
        Secret = GoogleCredentialSecret.create secret |> valueOrFail
        Username = GoogleExternalUsername.create username |> valueOrFail
    }

/// Fresh disposable database per test, no state shared. `connection=direct`, disposed before the
/// file is deleted. This is the shape a future per-integration LiteDB store copies - it took over
/// from CredentialStoreTests.withStore, cited in CLAUDE-project.md, when that file left with the
/// retired credentials integration. (That the delete actually succeeds after Dispose is asserted
/// on its own in GoogleDatabaseContextModuleTests, without a try/with.)
let private withStore (test: (unit -> Database.Types.GoogleCredentialsCollection) -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test context.GetCredentialCollection
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) ->
        failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

[<Fact; Trait("Level", "Integration")>]
let ``insertOne stores a credential and returns it with the identifier the store assigned`` () =
    withStore (fun getCollection ->
        // Act
        let stored =
            credential """{ "refresh_token": "1//abcDEF" }""" "person@gmail.com"
            |> GoogleCredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne"

        // Assert - every field, including the identifier the caller did not supply
        Assert.False(String.IsNullOrWhiteSpace(GoogleCredentialId.value stored.Id))
        Assert.Equal("""{ "refresh_token": "1//abcDEF" }""", GoogleCredentialSecret.value stored.Secret)
        Assert.Equal("person@gmail.com", GoogleExternalUsername.value stored.Username)
    )

[<Fact; Trait("Level", "Integration")>]
let ``getAll returns an empty list for a fresh database`` () =
    withStore (fun getCollection ->
        let stored = GoogleCredentialStore.getAll handleError getCollection () |> okOrFail "getAll"
        Assert.Empty stored
    )

[<Fact; Trait("Level", "Integration")>]
let ``insert then getAll returns the row with every field mapped back`` () =
    withStore (fun getCollection ->
        // Arrange
        let inserted =
            credential "ya29.a0Af" "person@gmail.com"
            |> GoogleCredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne"

        // Act
        let stored = GoogleCredentialStore.getAll handleError getCollection () |> okOrFail "getAll"

        // Assert
        let readBack = Assert.Single stored
        Assert.Equal(GoogleCredentialId.value inserted.Id, GoogleCredentialId.value readBack.Id)
        Assert.Equal("ya29.a0Af", GoogleCredentialSecret.value readBack.Secret)
        Assert.Equal("person@gmail.com", GoogleExternalUsername.value readBack.Username)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne changes the addressed row and a re-read reflects it`` () =
    withStore (fun getCollection ->
        // Arrange
        let inserted =
            credential "original" "original@gmail.com"
            |> GoogleCredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne"

        // Act
        let updated =
            edit (GoogleCredentialId.value inserted.Id) "rotated" "rotated@gmail.com"
            |> GoogleCredentialStore.updateOne handleError getCollection
            |> okOrFail "updateOne"

        // Assert
        match updated with
        | Some c ->
            Assert.Equal("rotated", GoogleCredentialSecret.value c.Secret)
            Assert.Equal("rotated@gmail.com", GoogleExternalUsername.value c.Username)
        | None -> Assert.Fail("Expected the row to be found")

        let readBack = Assert.Single(GoogleCredentialStore.getAll handleError getCollection () |> okOrFail "getAll")
        Assert.Equal(GoogleCredentialId.value inserted.Id, GoogleCredentialId.value readBack.Id)
        Assert.Equal("rotated", GoogleCredentialSecret.value readBack.Secret)
        Assert.Equal("rotated@gmail.com", GoogleExternalUsername.value readBack.Username)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne reports None for an identifier no row carries`` () =
    withStore (fun getCollection ->
        // Arrange - a well-formed ObjectId that was never stored
        credential "original" "original@gmail.com"
        |> GoogleCredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Act
        let updated =
            edit "507f1f77bcf86cd799439011" "ignored" "ignored@gmail.com"
            |> GoogleCredentialStore.updateOne handleError getCollection
            |> okOrFail "updateOne"

        // Assert - None, not a silent Ok
        Assert.True(Option.isNone updated, "expected None for an unknown identifier")

        let untouched = Assert.Single(GoogleCredentialStore.getAll handleError getCollection () |> okOrFail "getAll")
        Assert.Equal("original", GoogleCredentialSecret.value untouched.Secret)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne changes only the addressed row when two credentials look alike`` () =
    withStore (fun getCollection ->
        // Arrange
        let first =
            credential "first-secret" "first@gmail.com"
            |> GoogleCredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne first"

        let second =
            credential "second-secret" "second@gmail.com"
            |> GoogleCredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne second"

        // Act - address the second one
        edit (GoogleCredentialId.value second.Id) "second-rotated" "second@gmail.com"
        |> GoogleCredentialStore.updateOne handleError getCollection
        |> okOrFail "updateOne"
        |> ignore

        // Assert
        let stored = GoogleCredentialStore.getAll handleError getCollection () |> okOrFail "getAll"
        Assert.Equal(2, List.length stored)

        let reloadedFirst = stored |> List.find (fun c -> c.Id = first.Id)
        let reloadedSecond = stored |> List.find (fun c -> c.Id = second.Id)
        Assert.Equal("first-secret", GoogleCredentialSecret.value reloadedFirst.Secret)
        Assert.Equal("second-rotated", GoogleCredentialSecret.value reloadedSecond.Secret)
    )

[<Fact; Trait("Level", "Integration")>]
let ``a secret with newlines, non-ASCII and surrounding whitespace survives the round trip byte-for-byte`` () =
    withStore (fun getCollection ->
        // Arrange - the whole point of the local BsonMapper: this is where an OAuth refresh token
        // would be silently corrupted by the shared store's trimming
        let awkward = "  line one\nline two\ttabbed éàü \"quoted\" {json:true}  "

        credential awkward "person@gmail.com"
        |> GoogleCredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Act / Assert
        let readBack = Assert.Single(GoogleCredentialStore.getAll handleError getCollection () |> okOrFail "getAll")
        Assert.Equal(awkward, GoogleCredentialSecret.value readBack.Secret)
    )

[<Fact; Trait("Level", "Integration")>]
let ``getAll reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    // Arrange
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleCredentialsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    // Act
    match GoogleCredentialStore.getAll recordingHandleError failingGetter () with
    | Error ex ->
        Assert.Equal(
            ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.getAll,
            ex.ActionName
        )
        Assert.Equal("Failed to retrieve all credentials.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``insertOne reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleCredentialsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    match
        credential "secret" "person@gmail.com"
        |> GoogleCredentialStore.insertOne recordingHandleError failingGetter
    with
    | Error ex ->
        Assert.Equal(
            ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.insertOne,
            ex.ActionName
        )
        Assert.Equal("Failed to insert new credential.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``updateOne reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleCredentialsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    match
        edit "507f1f77bcf86cd799439011" "secret" "person@gmail.com"
        |> GoogleCredentialStore.updateOne recordingHandleError failingGetter
    with
    | Error ex ->
        Assert.Equal(
            ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.updateOne,
            ex.ActionName
        )
        Assert.Equal("Failed to update existing credential.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
