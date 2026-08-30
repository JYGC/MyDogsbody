module MyDogsbody.Tests.Integrations.Google.GoogleCredentialCharacterizationTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database

// Phase 3 of credentials-per-provider: the Phase 1 characterization assertions
// (Integrations/Credentials/CredentialCharacterizationTests.fs), retargeted at the NEW
// per-provider store. Every assertion that held against the retired shared store holds here too -
// EXCEPT one, and it is deliberate: the shared store trimmed leading/trailing whitespace from a
// secret (LiteDB BsonMapper.Global.TrimWhitespace), and the new store does NOT. It round-trips
// the secret byte-for-byte, which is what requirements.md actually asks for and what an OAuth
// refresh token (change #6) needs. See design.md -> "Decisions taken".

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private interiorNonAscii = "e-acute é e-grave è u-umlaut ü snowman ☃ interior"

let private credential secret username : ValidGoogleCredential =
    {
        Secret = GoogleCredentialSecret.create secret |> valueOrFail
        Username = GoogleExternalUsername.create username |> valueOrFail
    }

let private editOf id secret username : ValidGoogleCredentialEdit =
    {
        Id = GoogleCredentialId.create id |> valueOrFail
        Secret = GoogleCredentialSecret.create secret |> valueOrFail
        Username = GoogleExternalUsername.create username |> valueOrFail
    }

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
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

let private insert getCollection c =
    GoogleCredentialStore.insertOne handleError getCollection c |> okOrFail "insertOne"

let private readAll getCollection =
    GoogleCredentialStore.getAll handleError getCollection () |> okOrFail "getAll"

// ---------- the secret round-trips BYTE-FOR-BYTE, whitespace included ----------

[<Fact; Trait("Level", "Integration")>]
let ``a secret with embedded newlines, tabs and non-ASCII survives insert then read back byte-for-byte`` () =
    withStore (fun getCollection ->
        let secret = $"line one\nline two\ttabbed {interiorNonAscii}\r\n{{\"refresh_token\":\"1//aBc-_.~\"}}"
        insert getCollection (credential secret "person@gmail.com") |> ignore
        Assert.Equal(secret, GoogleCredentialSecret.value (Assert.Single(readAll getCollection)).Secret)
    )

[<Fact; Trait("Level", "Integration")>]
let ``a secret with leading and trailing whitespace survives the round trip unchanged`` () =
    // This is the assertion the retired shared store FAILED. The per-provider store's local
    // BsonMapper (TrimWhitespace off) is what makes it pass.
    withStore (fun getCollection ->
        let secret = "  1//0abc\tDEF   \n"
        insert getCollection (credential secret "person@gmail.com") |> ignore
        Assert.Equal(secret, GoogleCredentialSecret.value (Assert.Single(readAll getCollection)).Secret)
    )

[<Fact; Trait("Level", "Integration")>]
let ``a very long secret survives the round trip unchanged`` () =
    withStore (fun getCollection ->
        let longSecret = String.replicate 200 """{"k":"1//aBcDeF-_.~","n":42}"""
        insert getCollection (credential longSecret "person@gmail.com") |> ignore
        Assert.Equal(longSecret, GoogleCredentialSecret.value (Assert.Single(readAll getCollection)).Secret)
    )

// ---------- the username round-trips, the id surfaces as a string ----------

[<Fact; Trait("Level", "Integration")>]
let ``the external username round-trips unchanged and the store id surfaces as a non-empty string`` () =
    withStore (fun getCollection ->
        let stored = insert getCollection (credential "secret" "Person.Name+tag@sub.example.com")
        Assert.Equal("Person.Name+tag@sub.example.com", GoogleExternalUsername.value stored.Username)
        Assert.False(String.IsNullOrWhiteSpace(GoogleCredentialId.value stored.Id))

        let readBack = Assert.Single(readAll getCollection)
        Assert.Equal(GoogleCredentialId.value stored.Id, GoogleCredentialId.value readBack.Id)
    )

// ---------- an update reflects on re-read; a missing row is reported distinctly ----------

[<Fact; Trait("Level", "Integration")>]
let ``an update to an existing row reflects on re-read`` () =
    withStore (fun getCollection ->
        let inserted = insert getCollection (credential "original" "original@gmail.com")

        let updated =
            GoogleCredentialStore.updateOne handleError getCollection
                (editOf (GoogleCredentialId.value inserted.Id) "rotated" "rotated@gmail.com")
            |> okOrFail "updateOne"

        Assert.Equal(Some "rotated", updated |> Option.map (fun c -> GoogleCredentialSecret.value c.Secret))

        let readBack = Assert.Single(readAll getCollection)
        Assert.Equal("rotated", GoogleCredentialSecret.value readBack.Secret)
        Assert.Equal("rotated@gmail.com", GoogleExternalUsername.value readBack.Username)
    )

[<Fact; Trait("Level", "Integration")>]
let ``an update naming an identifier no row carries is reported as None, not a silent success`` () =
    withStore (fun getCollection ->
        insert getCollection (credential "original" "original@gmail.com") |> ignore

        let updated =
            GoogleCredentialStore.updateOne handleError getCollection
                (editOf "507f1f77bcf86cd799439011" "ignored" "ignored@gmail.com")
            |> okOrFail "updateOne"

        Assert.True(Option.isNone updated)
        Assert.Equal("original", GoogleCredentialSecret.value (Assert.Single(readAll getCollection)).Secret)
    )

// ---------- an empty secret or username is refused with a reason ----------

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``an empty secret is refused with a reason`` (entered: string) =
    match GoogleCredentialSecret.create entered with
    | Error reason -> Assert.Equal("Credentials must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``an empty username is refused with a reason`` (entered: string) =
    match GoogleExternalUsername.create entered with
    | Error reason -> Assert.Equal("Username must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- error shape and logging behaviour ----------

[<Fact; Trait("Level", "Integration")>]
let ``a store failure carries its declared action, message and a preserved inner exception, and is logged once`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleCredentialsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

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
let ``an unmappable stored row is a raised failure, logged, not a silent drop`` () =
    // The store's mapOrRaise: a row that cannot satisfy the integration's own rules (a null
    // secret written by an older build) is a data-integrity failure, caught and logged like any
    // other unexpected one - never returned as if the row were fine.
    withStore (fun getCollection ->
        let logged = ResizeArray<MyDogsbodyException>()
        let recordingHandleError = HandleErrorBuilder logged.Add

        // Arrange - insert a raw entity with a null secret, bypassing the constrained type
        getCollection().Insert(Database.Models.GoogleCredential(ExternalUsername = "person@gmail.com"))
        |> ignore

        // Act
        match GoogleCredentialStore.getAll recordingHandleError getCollection () with
        | Error ex ->
            Assert.Equal(
                ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.getAll,
                ex.ActionName
            )
            Assert.Single logged |> ignore
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")
    )
