module MyDogsbody.Tests.Domain.Credentials.ListCredentialsWorkflowTests

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

[<Fact; Trait("Level", "Unit")>]
let ``listCredentials returns every field of every stored credential`` () =
    // Arrange
    let load: LoadCredentials =
        fun () ->
            Ok
                [
                    storedCredential "aaaaaaaaaaaaaaaaaaaaaaaa" Google "google-secret" "person@gmail.com"
                    storedCredential "bbbbbbbbbbbbbbbbbbbbbbbb" Microsoft "ms-secret" "person@outlook.com"
                ]

    // Act
    let actual = ListCredentialsWorkflow.listCredentials load ()

    // Assert
    match actual with
    | Ok [ first; second ] ->
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", CredentialId.value first.Id)
        Assert.Equal(Google, first.Infrastructure)
        Assert.Equal("google-secret", CredentialSecret.value first.Credentials)
        Assert.Equal("person@gmail.com", ExternalUsername.value first.ExternalUsername)

        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbb", CredentialId.value second.Id)
        Assert.Equal(Microsoft, second.Infrastructure)
        Assert.Equal("ms-secret", CredentialSecret.value second.Credentials)
        Assert.Equal("person@outlook.com", ExternalUsername.value second.ExternalUsername)
    | Ok other -> Assert.Fail($"Expected exactly two credentials, got {List.length other}")
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``listCredentials returns an empty list for an empty store rather than an error`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Ok []

    // Act
    let actual = ListCredentialsWorkflow.listCredentials load ()

    // Assert
    Assert.Equal<Result<StoredCredential list, CredentialError>>(Ok [], actual)

[<Fact; Trait("Level", "Unit")>]
let ``listCredentials preserves the order the store returned`` () =
    // Arrange - the workflow adds no sort of its own, so the table shows store order
    let load: LoadCredentials =
        fun () ->
            Ok
                [
                    storedCredential "cccccccccccccccccccccccc" Microsoft "third" "third@outlook.com"
                    storedCredential "aaaaaaaaaaaaaaaaaaaaaaaa" Google "first" "first@gmail.com"
                ]

    // Act
    let actual = ListCredentialsWorkflow.listCredentials load ()

    // Assert
    match actual with
    | Ok credentials ->
        Assert.Equal<string list>(
            [ "cccccccccccccccccccccccc"; "aaaaaaaaaaaaaaaaaaaaaaaa" ],
            credentials |> List.map (fun credential -> CredentialId.value credential.Id)
        )
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``listCredentials returns the store's failure unchanged`` () =
    // Arrange
    let load: LoadCredentials = fun () -> Error (CredentialStoreFailed "database unreachable")

    // Act
    let actual = ListCredentialsWorkflow.listCredentials load ()

    // Assert
    Assert.Equal<Result<StoredCredential list, CredentialError>>(
        Error (CredentialStoreFailed "database unreachable"),
        actual
    )
