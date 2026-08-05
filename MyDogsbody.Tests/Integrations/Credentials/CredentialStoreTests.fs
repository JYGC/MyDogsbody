module MyDogsbody.Tests.Integrations.Credentials.CredentialStoreTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.Credentials
open MyDogsbody.Integrations.Credentials
open MyDogsbody.Integrations.Credentials.Database

/// No-op logger, so these tests never reach Logging.db.
let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private validCredential infrastructure secret username : ValidCredential =
    {
        Infrastructure = infrastructure
        Credentials = CredentialSecret.create secret |> valueOrFail
        ExternalUsername = ExternalUsername.create username |> valueOrFail
    }

let private edit id infrastructure secret username : ValidCredentialEdit =
    {
        Id = CredentialId.create id |> valueOrFail
        Infrastructure = infrastructure
        Credentials = CredentialSecret.create secret |> valueOrFail
        ExternalUsername = ExternalUsername.create username |> valueOrFail
    }

/// Fresh disposable database per test, no state shared between tests. The context now carries a
/// Dispose, so the handle is released deterministically and the temp file really goes away.
let private withStore (test: (unit -> Database.Types.CredentialsCollection) -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test context.GetCredentialCollection
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbody.Exceptions.Types.MyDogsbodyException) ->
        failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

[<Fact; Trait("Level", "Integration")>]
let ``insertOne stores a credential and returns it with the identifier the store assigned`` () =
    withStore (fun getCollection ->
        // Act
        let stored =
            validCredential Google """{ "refresh_token": "1//abcDEF" }""" "person@gmail.com"
            |> CredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne"

        // Assert - every field, including the identifier the domain did not supply
        Assert.False(String.IsNullOrWhiteSpace(CredentialId.value stored.Id))
        Assert.Equal(Google, stored.Infrastructure)
        Assert.Equal("""{ "refresh_token": "1//abcDEF" }""", CredentialSecret.value stored.Credentials)
        Assert.Equal("person@gmail.com", ExternalUsername.value stored.ExternalUsername)
    )

[<Fact; Trait("Level", "Integration")>]
let ``getAll returns an empty list for a fresh database`` () =
    withStore (fun getCollection ->
        // Act
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"

        // Assert
        Assert.Empty stored
    )

[<Fact; Trait("Level", "Integration")>]
let ``insert then getAll returns the row with every field mapped back`` () =
    withStore (fun getCollection ->
        // Arrange
        let inserted =
            validCredential Microsoft "ms-secret" "person@outlook.com"
            |> CredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne"

        // Act
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"

        // Assert
        let readBack = Assert.Single stored
        Assert.Equal(CredentialId.value inserted.Id, CredentialId.value readBack.Id)
        Assert.Equal(Microsoft, readBack.Infrastructure)
        Assert.Equal("ms-secret", CredentialSecret.value readBack.Credentials)
        Assert.Equal("person@outlook.com", ExternalUsername.value readBack.ExternalUsername)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne changes the addressed row and a re-read reflects it`` () =
    withStore (fun getCollection ->
        // Arrange
        let inserted =
            validCredential Google "original" "original@gmail.com"
            |> CredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne"

        // Act
        let updated =
            edit (CredentialId.value inserted.Id) Google "rotated" "rotated@gmail.com"
            |> CredentialStore.updateOne handleError getCollection
            |> okOrFail "updateOne"

        // Assert - reported as found, carrying the new values
        match updated with
        | Some credential ->
            Assert.Equal("rotated", CredentialSecret.value credential.Credentials)
            Assert.Equal("rotated@gmail.com", ExternalUsername.value credential.ExternalUsername)
        | None -> Assert.Fail("Expected the row to be found")

        // Assert - and the store really holds them
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"
        let readBack = Assert.Single stored
        Assert.Equal(CredentialId.value inserted.Id, CredentialId.value readBack.Id)
        Assert.Equal("rotated", CredentialSecret.value readBack.Credentials)
        Assert.Equal("rotated@gmail.com", ExternalUsername.value readBack.ExternalUsername)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne reports not found for an identifier no row carries`` () =
    withStore (fun getCollection ->
        // Arrange - a well-formed ObjectId that was never stored
        validCredential Google "original" "original@gmail.com"
        |> CredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Act
        let updated =
            edit "507f1f77bcf86cd799439011" Google "ignored" "ignored@gmail.com"
            |> CredentialStore.updateOne handleError getCollection
            |> okOrFail "updateOne"

        // Assert - None, not a silent Ok. The old repository returned success here.
        Assert.True(Option.isNone updated, "expected None for an unknown identifier")

        // Assert - and nothing was changed
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"
        let untouched = Assert.Single stored
        Assert.Equal("original", CredentialSecret.value untouched.Credentials)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne changes only the addressed row when two credentials share an infrastructure type`` () =
    withStore (fun getCollection ->
        // Arrange - the exact shape the old match-on-infrastructure-type repository got wrong
        let first =
            validCredential Google "first-secret" "first@gmail.com"
            |> CredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne first"

        let second =
            validCredential Google "second-secret" "second@gmail.com"
            |> CredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne second"

        // Act - address the second one
        edit (CredentialId.value second.Id) Google "second-rotated" "second@gmail.com"
        |> CredentialStore.updateOne handleError getCollection
        |> okOrFail "updateOne"
        |> ignore

        // Assert
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"
        Assert.Equal(2, List.length stored)

        let reloadedFirst =
            stored |> List.find (fun c -> CredentialId.value c.Id = CredentialId.value first.Id)

        let reloadedSecond =
            stored |> List.find (fun c -> CredentialId.value c.Id = CredentialId.value second.Id)

        Assert.Equal("first-secret", CredentialSecret.value reloadedFirst.Credentials)
        Assert.Equal("second-rotated", CredentialSecret.value reloadedSecond.Credentials)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne can move a credential to a different infrastructure type`` () =
    withStore (fun getCollection ->
        // Arrange
        let inserted =
            validCredential Google "secret" "person@gmail.com"
            |> CredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne"

        // Act
        edit (CredentialId.value inserted.Id) Microsoft "secret" "person@outlook.com"
        |> CredentialStore.updateOne handleError getCollection
        |> okOrFail "updateOne"
        |> ignore

        // Assert
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"
        let readBack = Assert.Single stored
        Assert.Equal(Microsoft, readBack.Infrastructure)
    )

[<Fact; Trait("Level", "Integration")>]
let ``getAll returns every stored credential`` () =
    withStore (fun getCollection ->
        // Arrange
        [
            validCredential Google "g" "g@gmail.com"
            validCredential Microsoft "m" "m@outlook.com"
        ]
        |> List.iter (fun credential ->
            CredentialStore.insertOne handleError getCollection credential
            |> okOrFail "insertOne"
            |> ignore
        )

        // Act
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"

        // Assert
        Assert.Equal(2, List.length stored)
        Assert.Contains(stored, fun c -> c.Infrastructure = Google)
        Assert.Contains(stored, fun c -> c.Infrastructure = Microsoft)
    )

[<Fact; Trait("Level", "Integration")>]
let ``a secret with newlines and non-ASCII characters survives the round trip unchanged`` () =
    withStore (fun getCollection ->
        // Arrange
        let awkward = "line one\nline two\ttabbed éàü \"quoted\" {json:true}"

        validCredential Google awkward "person@gmail.com"
        |> CredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Act
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"

        // Assert
        let readBack = Assert.Single stored
        Assert.Equal(awkward, CredentialSecret.value readBack.Credentials)
    )

[<Fact; Trait("Level", "Integration")>]
let ``getAll reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    // Arrange - a getter that fails is how infrastructure collapse looks from in here
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.CredentialsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    // Act
    let actual = CredentialStore.getAll recordingHandleError failingGetter ()

    // Assert
    match actual with
    | Error ex ->
        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.getAll,
            ex.ActionName
        )
        Assert.Equal("Failed to retrieve all credentials.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        // Unexpected failure: this one is logged.
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``insertOne reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    // Arrange
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.CredentialsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    // Act
    let actual =
        validCredential Google "secret" "person@gmail.com"
        |> CredentialStore.insertOne recordingHandleError failingGetter

    // Assert
    match actual with
    | Error ex ->
        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.insertOne,
            ex.ActionName
        )
        Assert.Equal("Failed to insert new credential.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``updateOne reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    // Arrange
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.CredentialsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    // Act
    let actual =
        edit "507f1f77bcf86cd799439011" Google "secret" "person@gmail.com"
        |> CredentialStore.updateOne recordingHandleError failingGetter

    // Assert
    match actual with
    | Error ex ->
        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.updateOne,
            ex.ActionName
        )
        Assert.Equal("Failed to update existing credential.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
