module MyDogsbody.Tests.Integrations.Google.GoogleCredentialTypesTests

open Xunit
open MyDogsbody.Integrations.Google

// Constrained types: holding one is the proof it was validated, so every create gets its own
// test - one accepted value, one rejected value per rule, and the rejection reason asserted.
// The reason strings are deliberately identical to the ones the retired shared store used.

[<Fact; Trait("Level", "Unit")>]
let ``GoogleCredentialSecret.create accepts a non-empty secret and preserves it exactly`` () =
    // Arrange - real credentials are JSON blobs, so nothing may be trimmed or re-encoded
    let entered = """  { "refresh_token": "1//abcDEF", "scope": "a b" }  """

    // Act
    match GoogleCredentialSecret.create entered with
    | Ok secret -> Assert.Equal(entered, GoogleCredentialSecret.value secret)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("\t\r\n")>]
let ``GoogleCredentialSecret.create rejects a missing secret with a reason`` (entered: string) =
    match GoogleCredentialSecret.create entered with
    | Error reason -> Assert.Equal("Credentials must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``GoogleExternalUsername.create accepts a non-empty username and preserves it exactly`` () =
    match GoogleExternalUsername.create "person@example.com" with
    | Ok username -> Assert.Equal("person@example.com", GoogleExternalUsername.value username)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``GoogleExternalUsername.create rejects a missing username with a reason`` (entered: string) =
    match GoogleExternalUsername.create entered with
    | Error reason -> Assert.Equal("Username must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``GoogleCredentialId.create accepts a non-empty identifier and preserves it exactly`` () =
    match GoogleCredentialId.create "507f1f77bcf86cd799439011" with
    | Ok id -> Assert.Equal("507f1f77bcf86cd799439011", GoogleCredentialId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``GoogleCredentialId.create rejects a missing identifier with a reason`` (entered: string) =
    match GoogleCredentialId.create entered with
    | Error reason -> Assert.Equal("Credential id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
