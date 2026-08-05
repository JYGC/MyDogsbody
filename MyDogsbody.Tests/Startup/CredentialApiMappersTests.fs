module MyDogsbody.Tests.Startup.CredentialApiMappersTests

open Xunit
open MyDogsbody.Enums
open MyDogsbody.Startup
open MyDogsbody.Spine.UseCases.Types
open MyDogsbody.UI.Types

[<Fact; Trait("Level", "Unit")>]
let ``toAddCredentialUseCaseTypeDto carries every field`` () =
    // Arrange
    let uiType: IntegrationCredentialUiTypeWithoutId =
        {
            InfrastructureType = InfrastructureType.Microsoft
            Credentials = """{ "token": "abc" }"""
            Username = "someone@example.com"
        }

    // Act
    let dto = CredentialApiMappers.toAddCredentialUseCaseTypeDto uiType

    // Assert — every field, not a shape check
    Assert.Equal(InfrastructureType.Microsoft, dto.InfrastructureType)
    Assert.Equal("""{ "token": "abc" }""", dto.Credentials)
    Assert.Equal("someone@example.com", dto.Username)

[<Fact; Trait("Level", "Unit")>]
let ``toCredentialUseCaseTypeDto carries every field including Id`` () =
    // Arrange
    let uiType: IntegrationCredentialUiType =
        {
            Id = "68b4f1c2e3a4b5c6d7e8f900"
            InfrastructureType = InfrastructureType.Google
            Credentials = "secret"
            Username = "user@gmail.com"
        }

    // Act
    let dto = CredentialApiMappers.toCredentialUseCaseTypeDto uiType

    // Assert
    Assert.Equal("68b4f1c2e3a4b5c6d7e8f900", dto.Id)
    Assert.Equal(InfrastructureType.Google, dto.InfrastructureType)
    Assert.Equal("secret", dto.Credentials)
    Assert.Equal("user@gmail.com", dto.Username)

[<Fact; Trait("Level", "Unit")>]
let ``toUiType carries every field including Id`` () =
    // Arrange
    let dto: CredentialUseCaseTypeDto =
        {
            Id = "68b4f1c2e3a4b5c6d7e8f901"
            InfrastructureType = InfrastructureType.Microsoft
            Credentials = "another secret"
            Username = "user@outlook.com"
        }

    // Act
    let uiType = CredentialApiMappers.toUiType dto

    // Assert
    Assert.Equal("68b4f1c2e3a4b5c6d7e8f901", uiType.Id)
    Assert.Equal(InfrastructureType.Microsoft, uiType.InfrastructureType)
    Assert.Equal("another secret", uiType.Credentials)
    Assert.Equal("user@outlook.com", uiType.Username)

[<Fact; Trait("Level", "Unit")>]
let ``toUiType and toCredentialUseCaseTypeDto are inverses`` () =
    // Arrange
    let original: IntegrationCredentialUiType =
        {
            Id = "68b4f1c2e3a4b5c6d7e8f902"
            InfrastructureType = InfrastructureType.Google
            Credentials = "round trip"
            Username = "round@trip.com"
        }

    // Act
    let roundTripped =
        original
        |> CredentialApiMappers.toCredentialUseCaseTypeDto
        |> CredentialApiMappers.toUiType

    // Assert
    Assert.Equal(original, roundTripped)
