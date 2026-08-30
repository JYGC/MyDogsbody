module MyDogsbody.Tests.Integrations.Credentials.CredentialCharacterizationTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Credentials
open MyDogsbody.Integrations.Credentials
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types
open MyDogsbody.Enums

// Change #5 (credentials-per-provider) is a pure refactor: the store where a credential lives
// moves from a shared MyDogsbody.Integrations.Credentials into each provider's own database.
//
// CLAUDE.md: "Existing behaviour you depend on but are not changing gets a characterization test
// before you change anything near it." This file pins the behaviour the move must reproduce,
// against the EXISTING store, before anything is built or deleted. Phase 3 copies every
// assertion here into GoogleCredentialCharacterizationTests.fs, retargeted at the new store; the
// two must agree except where noted. This file is then deleted (its subject no longer exists).

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private interiorNonAscii = "e-acute é e-grave è u-umlaut ü snowman ☃ interior"

let private validCredential secret username : ValidCredential =
    {
        Infrastructure = Google
        Credentials = CredentialSecret.create secret |> valueOrFail
        ExternalUsername = ExternalUsername.create username |> valueOrFail
    }

let private editOf id secret username : ValidCredentialEdit =
    {
        Id = CredentialId.create id |> valueOrFail
        Infrastructure = Google
        Credentials = CredentialSecret.create secret |> valueOrFail
        ExternalUsername = ExternalUsername.create username |> valueOrFail
    }

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
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

let private insert getCollection credential =
    CredentialStore.insertOne handleError getCollection credential |> okOrFail "insertOne"

let private readAll getCollection =
    CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"

// ---------- the secret round-trips (interior bytes exactly) ----------

[<Fact; Trait("Level", "Integration")>]
let ``a secret with embedded newlines, tabs and non-ASCII survives insert then read back byte-for-byte`` () =
    withStore (fun getCollection ->
        // Arrange
        let secret = $"line one\nline two\ttabbed {interiorNonAscii}\r\n{{\"refresh_token\":\"1//aBc-_.~\"}}"

        // Act
        insert getCollection (validCredential secret "person@gmail.com") |> ignore

        // Assert
        Assert.Equal(secret, CredentialSecret.value (Assert.Single(readAll getCollection)).Credentials)
    )

[<Fact; Trait("Level", "Integration")>]
let ``a very long secret survives the round trip unchanged`` () =
    withStore (fun getCollection ->
        // Arrange - an OAuth blob is easily a few KB
        let longSecret = String.replicate 200 """{"k":"1//aBcDeF-_.~","n":42}"""

        // Act
        insert getCollection (validCredential longSecret "person@gmail.com") |> ignore

        // Assert
        Assert.Equal(longSecret, CredentialSecret.value (Assert.Single(readAll getCollection)).Credentials)
    )

// CHARACTERIZATION FINDING, recorded here so the move is measured against reality rather than
// against what requirements.md assumed:
//
//   requirements.md's regression clause says the secret SHALL CONTINUE TO round-trip
//   "byte-for-byte unchanged, including leading and trailing whitespace". The shared store does
//   NOT do that today - LiteDB's BsonMapper.Global.TrimWhitespace defaults to true, so every
//   string property is trimmed on write: "  padded  " is stored and read back as "padded".
//
// The per-provider store built in Phase 2 uses a local BsonMapper with TrimWhitespace and
// EmptyStringToNull switched off, so it round-trips whitespace intact - which is what the
// requirement actually wants and what an OAuth refresh token (change #6) needs. This is the one
// assertion the new store deliberately does NOT reproduce: it does better. See design.md ->
// "Decisions taken" and outcome.md.
[<Fact; Trait("Level", "Integration")>]
let ``the shared store trims leading and trailing whitespace from the secret (characterization only)`` () =
    withStore (fun getCollection ->
        // Act
        insert getCollection (validCredential "  padded secret  " "person@gmail.com") |> ignore

        // Assert - the trimmed value, not the entered one
        Assert.Equal("padded secret", CredentialSecret.value (Assert.Single(readAll getCollection)).Credentials)
    )

// ---------- the username round-trips, the id surfaces as a string ----------

[<Fact; Trait("Level", "Integration")>]
let ``the external username round-trips unchanged and the store id surfaces as a non-empty string`` () =
    withStore (fun getCollection ->
        // Act
        let stored = insert getCollection (validCredential "secret" "Person.Name+tag@sub.example.com")

        // Assert
        Assert.Equal("Person.Name+tag@sub.example.com", ExternalUsername.value stored.ExternalUsername)
        Assert.False(String.IsNullOrWhiteSpace(CredentialId.value stored.Id))

        // Assert - and the same id comes back on re-read
        let readBack = Assert.Single(readAll getCollection)
        Assert.Equal(CredentialId.value stored.Id, CredentialId.value readBack.Id)
    )

// ---------- an update reflects on re-read; a missing row is reported distinctly ----------

[<Fact; Trait("Level", "Integration")>]
let ``an update to an existing row reflects on re-read`` () =
    withStore (fun getCollection ->
        // Arrange
        let inserted = insert getCollection (validCredential "original" "original@gmail.com")

        // Act
        let updated =
            CredentialStore.updateOne handleError getCollection
                (editOf (CredentialId.value inserted.Id) "rotated" "rotated@gmail.com")
            |> okOrFail "updateOne"

        // Assert
        Assert.Equal(Some "rotated", updated |> Option.map (fun c -> CredentialSecret.value c.Credentials))

        let readBack = Assert.Single(readAll getCollection)
        Assert.Equal("rotated", CredentialSecret.value readBack.Credentials)
        Assert.Equal("rotated@gmail.com", ExternalUsername.value readBack.ExternalUsername)
    )

[<Fact; Trait("Level", "Integration")>]
let ``an update naming an identifier no row carries is reported as None, not a silent success`` () =
    withStore (fun getCollection ->
        // Arrange
        insert getCollection (validCredential "original" "original@gmail.com") |> ignore

        // Act - a well-formed ObjectId that was never stored
        let updated =
            CredentialStore.updateOne handleError getCollection
                (editOf "507f1f77bcf86cd799439011" "ignored" "ignored@gmail.com")
            |> okOrFail "updateOne"

        // Assert
        Assert.True(Option.isNone updated)
        Assert.Equal("original", CredentialSecret.value (Assert.Single(readAll getCollection)).Credentials)
    )

// ---------- an empty secret or username is refused with a reason ----------
// The validation boundary the store sits behind: the store is only ever handed a ValidCredential,
// and CredentialSecret.create / ExternalUsername.create is where an empty value is turned away.

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``an empty secret is refused with a reason`` (entered: string) =
    match CredentialSecret.create entered with
    | Error reason -> Assert.Equal("Credentials must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``an empty username is refused with a reason`` (entered: string) =
    match ExternalUsername.create entered with
    | Error reason -> Assert.Equal("Username must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- error shape and logging behaviour ----------

[<Fact; Trait("Level", "Integration")>]
let ``a store failure carries its declared action, message and a preserved inner exception, and is logged once`` () =
    // Arrange - a getter that fails is how infrastructure collapse looks from in here
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.CredentialsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    // Act
    let actual = CredentialStore.getAll recordingHandleError failingGetter ()

    // Assert
    match actual with
    | Error ex ->
        Assert.Equal(
            ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.getAll,
            ex.ActionName
        )
        Assert.Equal("Failed to retrieve all credentials.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``an expected validation failure reaches the caller as an Error and is never logged`` () =
    // Arrange - an expected failure is returned as a value and never handed to writeLog
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"

    let api =
        CredentialApiFactory.createCredentialApi recordingHandleError context.GetCredentialCollection

    try
        // Act
        let actual =
            api.AddCredential
                {
                    InfrastructureType = InfrastructureType.Google
                    Credentials = ""
                    Username = "person@gmail.com"
                }

        // Assert
        match actual with
        | Error ex -> Assert.Equal("Credentials must not be empty.", ex.Message)
        | Ok () -> Assert.Fail("Expected Error, but got Ok")

        Assert.Empty logged
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``a store failure reaching the api is logged exactly once`` () =
    // Arrange
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter () : Database.Types.CredentialsCollection =
        raise (InvalidOperationException "database is gone")

    let api = CredentialApiFactory.createCredentialApi recordingHandleError failingGetter

    // Act
    let actual = api.GetAllCredentials()

    // Assert
    match actual with
    | Error ex -> Assert.Equal("Failed to retrieve all credentials.", ex.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

    Assert.Single logged |> ignore
