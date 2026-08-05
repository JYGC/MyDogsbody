module MyDogsbody.Tests.Startup.CredentialApiMappersTests

open System
open Xunit
open MyDogsbody.Enums
open MyDogsbody.Domain.Credentials
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// The top mapping point: domain type <-> MyDogsbody.UI.Types record. Total functions - a field
// copy has no failure mode - so there is nothing for a Result to carry.

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

let private allInfrastructureTypes =
    Enum.GetValues(typeof<InfrastructureType>)
    |> Seq.cast<InfrastructureType>
    |> Seq.toList

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedCredential carries every field of the UI record`` () =
    // Arrange
    let entered: IntegrationCredentialUiTypeWithoutId =
        {
            InfrastructureType = InfrastructureType.Google
            Credentials = """{ "refresh_token": "1//abcDEF" }"""
            Username = "person@gmail.com"
        }

    // Act
    let actual = CredentialApiMappers.toUnvalidatedCredential entered

    // Assert - every field, including the deliberate Username -> ExternalUsername rename
    Assert.Equal(Google, actual.Infrastructure)
    Assert.Equal("""{ "refresh_token": "1//abcDEF" }""", actual.Credentials)
    Assert.Equal("person@gmail.com", actual.ExternalUsername)

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedCredentialEdit carries every field of the UI record including the id`` () =
    // Arrange
    let entered: IntegrationCredentialUiType =
        {
            Id = "507f1f77bcf86cd799439011"
            InfrastructureType = InfrastructureType.Microsoft
            Credentials = "ms-secret"
            Username = "person@outlook.com"
        }

    // Act
    let actual = CredentialApiMappers.toUnvalidatedCredentialEdit entered

    // Assert
    Assert.Equal("507f1f77bcf86cd799439011", actual.Id)
    Assert.Equal(Microsoft, actual.Infrastructure)
    Assert.Equal("ms-secret", actual.Credentials)
    Assert.Equal("person@outlook.com", actual.ExternalUsername)

[<Fact; Trait("Level", "Unit")>]
let ``toUiType carries every field of the stored credential`` () =
    // Arrange
    let stored = storedCredential "507f1f77bcf86cd799439011" Google "google-secret" "person@gmail.com"

    // Act
    let actual = CredentialApiMappers.toUiType stored

    // Assert
    Assert.Equal("507f1f77bcf86cd799439011", actual.Id)
    Assert.Equal(InfrastructureType.Google, actual.InfrastructureType)
    Assert.Equal("google-secret", actual.Credentials)
    Assert.Equal("person@gmail.com", actual.Username)

[<Fact; Trait("Level", "Unit")>]
let ``a UI record survives the round trip through the domain unchanged`` () =
    // Arrange
    let entered: IntegrationCredentialUiType =
        {
            Id = "507f1f77bcf86cd799439011"
            InfrastructureType = InfrastructureType.Microsoft
            Credentials = "line one\nline two\ttabbed éàü"
            Username = "person@outlook.com"
        }

    // Act - out to the domain and back
    let unvalidated = CredentialApiMappers.toUnvalidatedCredentialEdit entered

    let stored =
        storedCredential
            unvalidated.Id
            unvalidated.Infrastructure
            unvalidated.Credentials
            unvalidated.ExternalUsername

    let actual = CredentialApiMappers.toUiType stored

    // Assert
    Assert.Equal(entered.Id, actual.Id)
    Assert.Equal(entered.InfrastructureType, actual.InfrastructureType)
    Assert.Equal(entered.Credentials, actual.Credentials)
    Assert.Equal(entered.Username, actual.Username)

[<Fact; Trait("Level", "Unit")>]
let ``every InfrastructureType member maps to a domain Infrastructure and back`` () =
    // Arrange - guards the enum/union translation against a member being added on one side
    // only. There are exactly two members today; this test is what notices a third.
    for infrastructureType in allInfrastructureTypes do
        // Act
        let entered: IntegrationCredentialUiTypeWithoutId =
            {
                InfrastructureType = infrastructureType
                Credentials = "secret"
                Username = "person@example.com"
            }

        let asDomain = (CredentialApiMappers.toUnvalidatedCredential entered).Infrastructure

        let backToUi =
            storedCredential "507f1f77bcf86cd799439011" asDomain "secret" "person@example.com"
            |> CredentialApiMappers.toUiType

        // Assert
        Assert.Equal(infrastructureType, backToUi.InfrastructureType)

[<Fact; Trait("Level", "Unit")>]
let ``the UI enumeration and the domain union have the same number of members`` () =
    // Arrange - the round-trip test above cannot see a domain case the UI has no member for.
    // This catches that direction.
    let domainCases =
        Reflection.FSharpType.GetUnionCases(typeof<Infrastructure>) |> Array.length

    // Assert
    Assert.Equal(List.length allInfrastructureTypes, domainCases)
