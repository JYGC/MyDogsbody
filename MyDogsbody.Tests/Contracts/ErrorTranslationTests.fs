module MyDogsbody.Tests.Contracts.ErrorTranslationTests

open System
open Xunit
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Credentials
open MyDogsbody.Startup

// The seam the UI's alerts are written from. Two directions, both asserted case by case:
//   inbound  - an adapter's MyDogsbodyException becomes a domain error case
//   outbound - a domain error case becomes the MyDogsbodyException the UI renders
// They meet in the composition root and nowhere else.

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private anAction = ActionNames.MyDogsbody.Startup.CredentialApi.addCredential

[<Fact; Trait("Level", "Contract")>]
let ``CredentialsInvalid becomes an exception carrying the reason and the declared action`` () =
    // Act
    let actual = CredentialApiMappers.toMyDogsbodyException anAction (CredentialsInvalid "Credentials must not be empty.")

    // Assert
    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("Credentials must not be empty.", actual.Message)

[<Fact; Trait("Level", "Contract")>]
let ``ExternalUsernameInvalid becomes an exception carrying the reason`` () =
    // Act
    let actual =
        CredentialApiMappers.toMyDogsbodyException anAction (ExternalUsernameInvalid "Username must not be empty.")

    // Assert
    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("Username must not be empty.", actual.Message)

[<Fact; Trait("Level", "Contract")>]
let ``CredentialIdInvalid becomes an exception carrying the reason`` () =
    // Act
    let actual =
        CredentialApiMappers.toMyDogsbodyException anAction (CredentialIdInvalid "Credential id must not be empty.")

    // Assert
    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("Credential id must not be empty.", actual.Message)

[<Fact; Trait("Level", "Contract")>]
let ``CredentialNotFound becomes an exception naming the identifier`` () =
    // Arrange - the payload is what the message is written from
    let id = CredentialId.create "507f1f77bcf86cd799439011" |> valueOrFail

    // Act
    let actual = CredentialApiMappers.toMyDogsbodyException anAction (CredentialNotFound id)

    // Assert
    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("No credential was found with id '507f1f77bcf86cd799439011'.", actual.Message)

[<Fact; Trait("Level", "Contract")>]
let ``CredentialStoreFailed becomes an exception carrying the store's message`` () =
    // Act
    let actual = CredentialApiMappers.toMyDogsbodyException anAction (CredentialStoreFailed "database is locked")

    // Assert
    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("database is locked", actual.Message)

[<Fact; Trait("Level", "Contract")>]
let ``every CredentialError case produces a non-empty message`` () =
    // Arrange - a new case added to the DU without a translation would otherwise reach the user
    // as an empty alert. Reflection walks the union so the test cannot fall behind it.
    let id = CredentialId.create "507f1f77bcf86cd799439011" |> valueOrFail

    let allCases: CredentialError list =
        [
            CredentialsInvalid "a"
            ExternalUsernameInvalid "b"
            CredentialIdInvalid "c"
            CredentialNotFound id
            CredentialStoreFailed "d"
        ]

    let declaredCases =
        Reflection.FSharpType.GetUnionCases(typeof<CredentialError>) |> Array.length

    // Assert - the list above covers the union
    Assert.Equal(declaredCases, List.length allCases)

    for case in allCases do
        let actual = CredentialApiMappers.toMyDogsbodyException anAction case
        Assert.False(String.IsNullOrWhiteSpace actual.Message, $"{case} produced an empty message")
        Assert.Equal(anAction, actual.ActionName)

[<Fact; Trait("Level", "Contract")>]
let ``a translated domain error carries no inner exception`` () =
    // Arrange - a domain error was never an exception. Wrapping one would make the UI's alert
    // carry a stack trace for something the user simply typed wrong.
    let actual = CredentialApiMappers.toMyDogsbodyException anAction (CredentialsInvalid "empty")

    // Assert
    Assert.Null actual.InnerException

[<Fact; Trait("Level", "Contract")>]
let ``an adapter exception becomes CredentialStoreFailed carrying its message`` () =
    // Arrange - inbound: this is how infrastructure failure reaches a workflow that may not
    // name MyDogsbodyException
    let adapterFailure =
        MyDogsbodyException(
            ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.getAll,
            "Failed to retrieve all credentials.",
            InvalidOperationException "disk gone"
        )

    // Act
    let actual = CredentialApiMappers.toCredentialError adapterFailure

    // Assert
    Assert.Equal(CredentialStoreFailed "Failed to retrieve all credentials.", actual)

[<Fact; Trait("Level", "Contract")>]
let ``the inbound and outbound translations round trip an adapter message`` () =
    // Arrange
    let adapterFailure =
        MyDogsbodyException(
            ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.insertOne,
            "Failed to insert new credential.",
            InvalidOperationException "disk gone"
        )

    // Act - in to the domain, back out to the UI
    let actual =
        adapterFailure
        |> CredentialApiMappers.toCredentialError
        |> CredentialApiMappers.toMyDogsbodyException anAction

    // Assert - the message the adapter wrote is the message the user sees
    Assert.Equal("Failed to insert new credential.", actual.Message)
