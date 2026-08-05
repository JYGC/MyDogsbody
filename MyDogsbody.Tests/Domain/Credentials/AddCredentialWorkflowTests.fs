module MyDogsbody.Tests.Domain.Credentials.AddCredentialWorkflowTests

open Xunit
open MyDogsbody.Domain.Credentials

// Dependencies are function types, so every fake here is a lambda: no mocking framework, no
// handleError, no temp file, no fixture. That is what the architecture buys.

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private storedCredential id infrastructure secret username : StoredCredential =
    {
        Id = CredentialId.create id |> valueOrFail
        Infrastructure = infrastructure
        Credentials = CredentialSecret.create secret |> valueOrFail
        ExternalUsername = ExternalUsername.create username |> valueOrFail
    }

/// A SaveCredential that records what it was handed, so "the store was never reached" is
/// assertable rather than assumed.
let private recordingSave () =
    let received = ResizeArray<ValidCredential>()

    let save: SaveCredential =
        fun credential ->
            received.Add credential
            Ok
                {
                    Id = CredentialId.create "507f1f77bcf86cd799439011" |> valueOrFail
                    Infrastructure = credential.Infrastructure
                    Credentials = credential.Credentials
                    ExternalUsername = credential.ExternalUsername
                }

    save, received

[<Fact; Trait("Level", "Unit")>]
let ``addCredential saves a valid credential and returns every stored field`` () =
    // Arrange
    let save, received = recordingSave ()

    let entered: UnvalidatedCredential =
        {
            Infrastructure = Google
            Credentials = """{ "refresh_token": "1//abcDEF" }"""
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = AddCredentialWorkflow.addCredential save entered

    // Assert - every field of the output, unwrapped. Result.isOk would prove nothing.
    match actual with
    | Ok stored ->
        Assert.Equal("507f1f77bcf86cd799439011", CredentialId.value stored.Id)
        Assert.Equal(Google, stored.Infrastructure)
        Assert.Equal("""{ "refresh_token": "1//abcDEF" }""", CredentialSecret.value stored.Credentials)
        Assert.Equal("person@gmail.com", ExternalUsername.value stored.ExternalUsername)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    // Assert - what the workflow handed the store is the validated shape, field for field
    let saved = Assert.Single received
    Assert.Equal(Google, saved.Infrastructure)
    Assert.Equal("""{ "refresh_token": "1//abcDEF" }""", CredentialSecret.value saved.Credentials)
    Assert.Equal("person@gmail.com", ExternalUsername.value saved.ExternalUsername)

[<Fact; Trait("Level", "Unit")>]
let ``addCredential preserves the infrastructure it was given`` () =
    // Arrange - guards the enum-to-DU translation against collapsing to a default
    let save, _ = recordingSave ()

    let entered: UnvalidatedCredential =
        {
            Infrastructure = Microsoft
            Credentials = "secret"
            ExternalUsername = "person@outlook.com"
        }

    // Act
    let actual = AddCredentialWorkflow.addCredential save entered

    // Assert
    match actual with
    | Ok stored -> Assert.Equal(Microsoft, stored.Infrastructure)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``addCredential rejects an empty secret and never reaches the store`` () =
    // Arrange
    let save, received = recordingSave ()

    let entered: UnvalidatedCredential =
        {
            Infrastructure = Google
            Credentials = "   "
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = AddCredentialWorkflow.addCredential save entered

    // Assert - the exact case and its payload; the payload is what the message is written from
    Assert.Equal(Error (CredentialsInvalid "Credentials must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addCredential rejects an empty username and never reaches the store`` () =
    // Arrange
    let save, received = recordingSave ()

    let entered: UnvalidatedCredential =
        {
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = ""
        }

    // Act
    let actual = AddCredentialWorkflow.addCredential save entered

    // Assert
    Assert.Equal(Error (ExternalUsernameInvalid "Username must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addCredential reports the secret first when both fields are missing`` () =
    // Arrange - pins which failure the user is told about, rather than leaving it to bind order
    let save, received = recordingSave ()

    let entered: UnvalidatedCredential =
        {
            Infrastructure = Google
            Credentials = ""
            ExternalUsername = ""
        }

    // Act
    let actual = AddCredentialWorkflow.addCredential save entered

    // Assert
    Assert.Equal(Error (CredentialsInvalid "Credentials must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addCredential returns the store's failure unchanged`` () =
    // Arrange
    let save: SaveCredential = fun _ -> Error (CredentialStoreFailed "disk full")

    let entered: UnvalidatedCredential =
        {
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = AddCredentialWorkflow.addCredential save entered

    // Assert
    Assert.Equal(Error (CredentialStoreFailed "disk full"), actual)

[<Fact; Trait("Level", "Unit")>]
let ``addCredential does not re-check validation on the value the store returned`` () =
    // Arrange - holding the constrained type is the proof; nothing downstream re-validates.
    // A store that echoes a different-but-valid value must be returned as-is.
    let save: SaveCredential =
        fun credential ->
            Ok (storedCredential "507f1f77bcf86cd799439011" credential.Infrastructure "rewritten" "rewritten@example.com")

    let entered: UnvalidatedCredential =
        {
            Infrastructure = Microsoft
            Credentials = "secret"
            ExternalUsername = "person@outlook.com"
        }

    // Act
    let actual = AddCredentialWorkflow.addCredential save entered

    // Assert
    match actual with
    | Ok stored ->
        Assert.Equal("rewritten", CredentialSecret.value stored.Credentials)
        Assert.Equal("rewritten@example.com", ExternalUsername.value stored.ExternalUsername)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
