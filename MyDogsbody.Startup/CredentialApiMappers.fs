/// Maps between the UI's records and the Spine use-case DTOs.
///
/// Total functions: a field copy has no failure mode, so there is nothing for a Result to
/// carry. Deliberately kept in their own file with no module-level bindings, so a test can
/// reach them without Startup.fs opening a database.
module MyDogsbody.Startup.CredentialApiMappers

open MyDogsbody.Spine.UseCases.Types
open MyDogsbody.UI.Types

let toAddCredentialUseCaseTypeDto
  (uiType: IntegrationCredentialUiTypeWithoutId)
  : AddCredentialUseCaseTypeDto =
    {
        InfrastructureType = uiType.InfrastructureType
        Credentials = uiType.Credentials
        Username = uiType.Username
    }

let toCredentialUseCaseTypeDto
  (uiType: IntegrationCredentialUiType)
  : CredentialUseCaseTypeDto =
    {
        Id = uiType.Id
        InfrastructureType = uiType.InfrastructureType
        Credentials = uiType.Credentials
        Username = uiType.Username
    }

let toUiType
  (dto: CredentialUseCaseTypeDto)
  : IntegrationCredentialUiType =
    {
        Id = dto.Id
        InfrastructureType = dto.InfrastructureType
        Credentials = dto.Credentials
        Username = dto.Username
    }
