module MyDogsbody.Tests.Domain.Credentials.EditCredentialWorkflowTests

open Xunit
open MyDogsbody.Domain.Credentials

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

let private googleRow = storedCredential "aaaaaaaaaaaaaaaaaaaaaaaa" Google "google-secret" "person@gmail.com"
let private microsoftRow = storedCredential "bbbbbbbbbbbbbbbbbbbbbbbb" Microsoft "ms-secret" "person@outlook.com"

/// Records every update attempt, so "the store was never reached" is assertable.
let private recordingUpdate (outcome: ValidCredentialEdit -> Result<StoredCredential option, CredentialError>) =
    let received = ResizeArray<ValidCredentialEdit>()

    let update: UpdateCredential =
        fun edit ->
            received.Add edit
            outcome edit

    update, received

let private updateSucceeds: ValidCredentialEdit -> Result<StoredCredential option, CredentialError> =
    fun edit ->
        Ok (
            Some
                {
                    Id = edit.Id
                    Infrastructure = edit.Infrastructure
                    Credentials = edit.Credentials
                    ExternalUsername = edit.ExternalUsername
                }
        )

[<Fact; Trait("Level", "Unit")>]
let ``editCredential updates an existing credential and returns every stored field`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Ok [ googleRow; microsoftRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "aaaaaaaaaaaaaaaaaaaaaaaa"
            Infrastructure = Google
            Credentials = "rotated-secret"
            ExternalUsername = "rotated@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert - every field of the output
    match actual with
    | Ok stored ->
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", CredentialId.value stored.Id)
        Assert.Equal(Google, stored.Infrastructure)
        Assert.Equal("rotated-secret", CredentialSecret.value stored.Credentials)
        Assert.Equal("rotated@gmail.com", ExternalUsername.value stored.ExternalUsername)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    // Assert - the store was addressed by identifier, carrying the validated values
    let attempted = Assert.Single received
    Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", CredentialId.value attempted.Id)
    Assert.Equal("rotated-secret", CredentialSecret.value attempted.Credentials)

[<Fact; Trait("Level", "Unit")>]
let ``editCredential addresses only the submitted row when two share an infrastructure type`` () =
    // Arrange - the defect this change fixes: the old store matched on infrastructure type and
    // ignored the id, so editing one of these updated the wrong one.
    let firstGoogle = storedCredential "aaaaaaaaaaaaaaaaaaaaaaaa" Google "first-secret" "first@gmail.com"
    let secondGoogle = storedCredential "cccccccccccccccccccccccc" Google "second-secret" "second@gmail.com"

    let load: LoadCredentials = fun () -> Ok [ firstGoogle; secondGoogle ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "cccccccccccccccccccccccc"
            Infrastructure = Google
            Credentials = "second-rotated"
            ExternalUsername = "second@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert - the second row, not the first match on type
    let attempted = Assert.Single received
    Assert.Equal("cccccccccccccccccccccccc", CredentialId.value attempted.Id)

    match actual with
    | Ok stored -> Assert.Equal("cccccccccccccccccccccccc", CredentialId.value stored.Id)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``editCredential reports not found when no stored credential has that identifier`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Ok [ googleRow; microsoftRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "dddddddddddddddddddddddd"
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert - the case and its payload; the id is what the message names
    let expectedId = CredentialId.create "dddddddddddddddddddddddd" |> valueOrFail
    Assert.Equal(Error (CredentialNotFound expectedId), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editCredential reports not found against an empty store`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Ok []
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "aaaaaaaaaaaaaaaaaaaaaaaa"
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert - it must not silently succeed, which is what the old store did
    let expectedId = CredentialId.create "aaaaaaaaaaaaaaaaaaaaaaaa" |> valueOrFail
    Assert.Equal(Error (CredentialNotFound expectedId), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editCredential reports not found when the store loses the row between load and update`` () =
    // Arrange - load says it is there, the update finds nothing. The race is accepted, but it
    // must surface as not-found rather than as success.
    let load: LoadCredentials = fun () -> Ok [ googleRow ]
    let update, received = recordingUpdate (fun _ -> Ok None)

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "aaaaaaaaaaaaaaaaaaaaaaaa"
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert
    let expectedId = CredentialId.create "aaaaaaaaaaaaaaaaaaaaaaaa" |> valueOrFail
    Assert.Equal(Error (CredentialNotFound expectedId), actual)
    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``editCredential rejects an empty identifier and never loads or updates`` () =
    // Arrange
    let mutable loaded = false

    let load: LoadCredentials =
        fun () ->
            loaded <- true
            Ok [ googleRow ]

    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "  "
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert
    Assert.Equal(Error (CredentialIdInvalid "Credential id must not be empty."), actual)
    Assert.False(loaded, "validation failed, so the store must not have been read")
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editCredential rejects an empty secret and never updates`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Ok [ googleRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "aaaaaaaaaaaaaaaaaaaaaaaa"
            Infrastructure = Google
            Credentials = ""
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert
    Assert.Equal(Error (CredentialsInvalid "Credentials must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editCredential rejects an empty username and never updates`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Ok [ googleRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "aaaaaaaaaaaaaaaaaaaaaaaa"
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = "   "
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert
    Assert.Equal(Error (ExternalUsernameInvalid "Username must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editCredential returns a load failure unchanged and never updates`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Error (CredentialStoreFailed "database locked")
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "aaaaaaaaaaaaaaaaaaaaaaaa"
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert
    Assert.Equal(Error (CredentialStoreFailed "database locked"), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editCredential returns an update failure unchanged`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Ok [ googleRow ]
    let update, _ = recordingUpdate (fun _ -> Error (CredentialStoreFailed "write failed"))

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "aaaaaaaaaaaaaaaaaaaaaaaa"
            Infrastructure = Google
            Credentials = "secret"
            ExternalUsername = "person@gmail.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert
    Assert.Equal(Error (CredentialStoreFailed "write failed"), actual)

[<Fact; Trait("Level", "Unit")>]
let ``editCredential can change the infrastructure of an existing credential`` () =
    // Arrange - the row is found by identifier, so its type is free to change
    let load: LoadCredentials = fun () -> Ok [ googleRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedCredentialEdit =
        {
            Id = "aaaaaaaaaaaaaaaaaaaaaaaa"
            Infrastructure = Microsoft
            Credentials = "secret"
            ExternalUsername = "person@outlook.com"
        }

    // Act
    let actual = EditCredentialWorkflow.editCredential load update submitted

    // Assert
    let attempted = Assert.Single received
    Assert.Equal(Microsoft, attempted.Infrastructure)

    match actual with
    | Ok stored -> Assert.Equal(Microsoft, stored.Infrastructure)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
