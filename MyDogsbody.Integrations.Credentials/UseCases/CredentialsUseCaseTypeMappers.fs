module MyDogsbody.Integrations.Credentials.UseCases.CredentialsUseCaseTypeMappers

open MyDogsbody.Integrations.Credentials.Repositories.Types
open MyDogsbody.Integrations.Credentials.UseCases.Types
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types

let mapNewCredentialUseCaseTypeDtoToNewCredentialRepositoryTypeDto
  (handleError: HandleErrorBuilder)
  (dto: NewCredentialUseCaseTypeDto)
  : Result<NewCredentialRepositoryTypeDto, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.UseCases.Types.Mappers.mapNewCredentialUseCaseTypeDtoToNewCredentialRepositoryTypeDto;
    handleError {
        try
            return {
                InfrastructureType = dto.InfrastructureType
                Credentials = dto.Credentials
                ExternalUsername = dto.ExternalUsername
            }
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to map NewCredentialUseCaseTypeDto to NewCredentialRepositoryTypeDto.",
                ex
            )
    }

let mapExistingCredentialUseCaseTypeDtoToExistingCredentialRepositoryTypeDto
  (handleError: HandleErrorBuilder)
  (dto: ExistingCredentialUseCaseTypeDto)
  : Result<ExistingCredentialRepositoryTypeDto, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.UseCases.Types.Mappers.mapExistingCredentialUseCaseTypeDtoToExistingCredentialRepositoryTypeDto;
    handleError {
        try
            return {
                Id = dto.Id
                InfrastructureType = dto.InfrastructureType
                Credentials = dto.Credentials
                ExternalUsername = dto.ExternalUsername
            }
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to map ExistingCredentialUseCaseTypeDto to ExistingCredentialRepositoryTypeDto.",
                ex
            )
    }

let mapExistingCredentialRepositoryTypeDtosToExistingCredentialUseCaseTypeDtos
  (handleError: HandleErrorBuilder)
  (dtos: ExistingCredentialRepositoryTypeDto list)
  : Result<ExistingCredentialUseCaseTypeDto list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.UseCases.Types.Mappers.mapExistingCredentialRepositoryTypeDtoToExistingCredentialUseCaseTypeDto;
    handleError {
        try
            return
                dtos
                |> List.map (fun dto ->
                    {
                    Id = dto.Id
                    InfrastructureType = dto.InfrastructureType
                    Credentials = dto.Credentials
                    ExternalUsername = dto.ExternalUsername
                    }
                )
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to map ExistingCredentialRepositoryTypeDto to ExistingCredentialUseCaseTypeDto.",
                ex
            )
    }