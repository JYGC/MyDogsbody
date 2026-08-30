module MyDogsbody.Tests.Contracts.GoogleCredentialBoundaryMapperTests

open Xunit
open LiteDB
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database.Models

// The bottom mapping point of the Google integration, asserted field-for-field in both
// directions - CLAUDE.md: "Every mapper at a ring boundary is asserted field-for-field in both
// directions, with deliberate renames asserted as renames."
//
// GoogleCredentialEntityMappers is new code in this change; it took over from the retired
// CredentialEntityMappers (Contracts/CredentialBoundaryMapperTests.fs), minus the
// InfrastructureType pair the shared store needed. The store-level tests exercise it only
// transitively; this file pins it directly, with no database in the way.

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private aValidCredential secret username : ValidGoogleCredential =
    {
        Secret = GoogleCredentialSecret.create secret |> valueOrFail
        Username = GoogleExternalUsername.create username |> valueOrFail
    }

// ---------- integration type -> entity ----------

[<Fact; Trait("Level", "Contract")>]
let ``toNewEntity carries every field of a valid credential onto the entity`` () =
    // Act
    let actual =
        GoogleCredentialEntityMappers.toNewEntity (aValidCredential "google-secret" "person@gmail.com")

    // Assert
    Assert.Equal("google-secret", actual.Credentials)
    Assert.Equal("person@gmail.com", actual.ExternalUsername)

[<Fact; Trait("Level", "Contract")>]
let ``toNewEntity leaves the identifier unset, for the store to assign`` () =
    // Act
    let actual =
        GoogleCredentialEntityMappers.toNewEntity (aValidCredential "secret" "person@gmail.com")

    // Assert - LiteDB stamps the id on insert; the mapper must not guess one
    Assert.Equal(ObjectId.Empty, actual.Id)

// ---------- entity -> integration type ----------

[<Fact; Trait("Level", "Contract")>]
let ``toStoredCredential carries every field of an entity back to the stored type`` () =
    // Arrange
    let entity =
        GoogleCredential(
            Id = ObjectId "507f1f77bcf86cd799439011",
            Credentials = "ya29.a0Af",
            ExternalUsername = "person@gmail.com"
        )

    // Act
    match GoogleCredentialEntityMappers.toStoredCredential entity with
    | Ok stored ->
        // Assert
        Assert.Equal("507f1f77bcf86cd799439011", GoogleCredentialId.value stored.Id)
        Assert.Equal("ya29.a0Af", GoogleCredentialSecret.value stored.Secret)
        Assert.Equal("person@gmail.com", GoogleExternalUsername.value stored.Username)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Contract")>]
[<InlineData(null, "person@gmail.com", "Credentials must not be empty.")>]
[<InlineData("secret", null, "Username must not be empty.")>]
let ``toStoredCredential rejects a row that cannot satisfy the integration's rules``
    (secret: string)
    (username: string)
    (expectedReason: string)
    =
    // Arrange - LiteDB is schemaless, so a document written by an older build can carry a null
    // where a constrained type is required
    let entity =
        GoogleCredential(
            Id = ObjectId "507f1f77bcf86cd799439011",
            Credentials = secret,
            ExternalUsername = username
        )

    // Act / Assert
    match GoogleCredentialEntityMappers.toStoredCredential entity with
    | Error reason -> Assert.Equal(expectedReason, reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- edit -> entity ----------

[<Fact; Trait("Level", "Contract")>]
let ``applyEdit overwrites the secret and username but leaves the identifier untouched`` () =
    // Arrange
    let entity =
        GoogleCredential(
            Id = ObjectId "507f1f77bcf86cd799439011",
            Credentials = "old-secret",
            ExternalUsername = "old@gmail.com"
        )

    let edit: ValidGoogleCredentialEdit =
        {
            Id = GoogleCredentialId.create "507f1f77bcf86cd799439011" |> valueOrFail
            Secret = GoogleCredentialSecret.create "new-secret" |> valueOrFail
            Username = GoogleExternalUsername.create "new@gmail.com" |> valueOrFail
        }

    // Act
    let actual = GoogleCredentialEntityMappers.applyEdit edit entity

    // Assert - the row is addressed by its id, so the id must survive the edit
    Assert.Equal("new-secret", actual.Credentials)
    Assert.Equal("new@gmail.com", actual.ExternalUsername)
    Assert.Equal("507f1f77bcf86cd799439011", string actual.Id)

// ---------- round trip and renames ----------

[<Fact; Trait("Level", "Contract")>]
let ``the bottom mapper round trips a credential unchanged, awkward bytes included`` () =
    // Arrange - a secret is a JSON blob or an OAuth token; nothing may be trimmed or re-encoded
    let awkward = "  line one\nline two\ttabbed éàü \"quoted\" {json:true}  "
    let entity = GoogleCredentialEntityMappers.toNewEntity (aValidCredential awkward "person@gmail.com")
    entity.Id <- ObjectId "507f1f77bcf86cd799439011"

    // Act
    match GoogleCredentialEntityMappers.toStoredCredential entity with
    | Ok stored ->
        // Assert - the constrained types survive the entity's plain string properties
        Assert.Equal(awkward, GoogleCredentialSecret.value stored.Secret)
        Assert.Equal("person@gmail.com", GoogleExternalUsername.value stored.Username)
        Assert.Equal("507f1f77bcf86cd799439011", GoogleCredentialId.value stored.Id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Contract")>]
let ``the secret and username are renamed at the bottom boundary and nowhere else`` () =
    // The integration type reads Secret / Username; the persisted entity keeps the retired
    // store's Credentials / ExternalUsername property names. That is a deliberate rename, and it
    // is asserted here as a rename so a "tidy-up" that aligns the names cannot pass unnoticed.
    let entity =
        GoogleCredentialEntityMappers.toNewEntity (aValidCredential "the-secret" "the-user@gmail.com")

    Assert.Equal("the-secret", entity.Credentials)
    Assert.Equal("the-user@gmail.com", entity.ExternalUsername)

    entity.Id <- ObjectId "507f1f77bcf86cd799439011"
    let stored = GoogleCredentialEntityMappers.toStoredCredential entity |> valueOrFail

    Assert.Equal("the-secret", GoogleCredentialSecret.value stored.Secret)
    Assert.Equal("the-user@gmail.com", GoogleExternalUsername.value stored.Username)

[<Fact; Trait("Level", "Contract")>]
let ``toObjectId turns a well-formed identifier into the store's key type unchanged`` () =
    // Act
    let actual =
        GoogleCredentialEntityMappers.toObjectId (GoogleCredentialId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    // Assert
    Assert.Equal(ObjectId "507f1f77bcf86cd799439011", actual)
    Assert.Equal("507f1f77bcf86cd799439011", string actual)
