module MyDogsbody.Tests.Domain.Credentials.CredentialsTypesTests

open Xunit
open MyDogsbody.Domain.Credentials

// Constrained types: holding one is the proof it was validated, so every create gets its own
// test - one accepted value, one rejected value per rule, and the rejection reason asserted.
// An unasserted reason is an untested message.

[<Fact; Trait("Level", "Unit")>]
let ``CredentialSecret.create accepts a non-empty secret and preserves it exactly`` () =
    // Arrange - real credentials are JSON blobs, so nothing may be trimmed or re-encoded
    let entered = """{ "refresh_token": "1//abcDEF", "scope": "a b" }"""

    // Act
    let actual = CredentialSecret.create entered

    // Assert
    match actual with
    | Ok secret -> Assert.Equal(entered, CredentialSecret.value secret)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("\t\r\n")>]
let ``CredentialSecret.create rejects a missing secret with a reason`` (entered: string) =
    // Act
    let actual = CredentialSecret.create entered

    // Assert
    match actual with
    | Error reason -> Assert.Equal("Credentials must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``ExternalUsername.create accepts a non-empty username and preserves it exactly`` () =
    // Arrange
    let entered = "person@example.com"

    // Act
    let actual = ExternalUsername.create entered

    // Assert
    match actual with
    | Ok username -> Assert.Equal(entered, ExternalUsername.value username)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``ExternalUsername.create rejects a missing username with a reason`` (entered: string) =
    // Act
    let actual = ExternalUsername.create entered

    // Assert
    match actual with
    | Error reason -> Assert.Equal("Username must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``CredentialId.create accepts a non-empty identifier and preserves it exactly`` () =
    // Arrange - LiteDB ObjectIds arrive as 24 hex characters
    let entered = "507f1f77bcf86cd799439011"

    // Act
    let actual = CredentialId.create entered

    // Assert
    match actual with
    | Ok id -> Assert.Equal(entered, CredentialId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``CredentialId.create rejects a missing identifier with a reason`` (entered: string) =
    // Act
    let actual = CredentialId.create entered

    // Assert
    match actual with
    | Error reason -> Assert.Equal("Credential id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``a constrained value survives create then value unchanged`` () =
    // Arrange - the round trip the store's string column depends on
    let secret = "line one\nline two\ttabbed éàü"

    // Act
    let actual =
        secret
        |> CredentialSecret.create
        |> Result.map CredentialSecret.value

    // Assert
    Assert.Equal(Ok secret, actual)
