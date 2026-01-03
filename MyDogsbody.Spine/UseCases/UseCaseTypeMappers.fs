module MyDogsbody.Spine.UseCases.UseCaseTypeMappers

open MyDogsbody.Builders
open MyDogsbody.Integrations.Credentials.UseCases.Types
open MyDogsbody.Exceptions.Types
open MyDogsbody.Spine.UseCases.Types
open MyDogsbody.Spine.Domains.Types

let mapAddCredentialUseCaseTypeDtoToNewCredentialUseCaseTypeDto
  (handleError: HandleErrorBuilder)
  (useCaseDto: AddCredentialUseCaseTypeDto)
  : Result<NewCredentialUseCaseTypeDto, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.UseCases.UseCaseTypeMappers.mapAddCredentialUseCaseTypeDtoToNewCredentialUseCaseTypeDto
    handleError {
        try
            return {
                InfrastructureType = useCaseDto.InfrastructureType
                Credentials = useCaseDto.Credentials
                ExternalUsername = useCaseDto.Username
            }
        with ex ->
            return! MyDogsbodyException(
                action,
                "Failed to map AddCredentialUseCaseTypeDto to NewCredentialUseCaseTypeDto.",
                ex
            )
    }

let mapAddCredentialUseCaseTypeDtoToAddCredentialDomainTypeDto
  (handleError: HandleErrorBuilder)
  (useCaseDto: AddCredentialUseCaseTypeDto)
  : Result<AddCredentialDomainTypeDto, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.UseCases.UseCaseTypeMappers.mapAddCredentialUseCaseTypeDtoToAddCredentialDomainTypeDto
    handleError {
        try
            return {
                InfrastructureType = useCaseDto.InfrastructureType
                Credentials = useCaseDto.Credentials
                ExternalUsername = useCaseDto.Username
            }
        with ex ->
            return! MyDogsbodyException(
                action,
                "Failed to map AddCredentialUseCaseTypeDto to AddCredentialDomainTypeDto.",
                ex
            )
    }

let mapCredentialUseCaseTypeDtoToCredentialDomainTypeDto
  (handleError: HandleErrorBuilder)
  (useCaseDto: CredentialUseCaseTypeDto)
  : Result<CredentialDomainTypeDto, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.UseCases.UseCaseTypeMappers.mapCredentialUseCaseTypeDtoToCredentialDomainTypeDto
    handleError {
        try
            return {
                Id = useCaseDto.Id
                InfrastructureType = useCaseDto.InfrastructureType
                Credentials = useCaseDto.Credentials
                ExternalUsername = useCaseDto.Username
            }
        with ex ->
            return! MyDogsbodyException(
                action,
                "Failed to map CredentialUseCaseTypeDto to CredentialDomainTypeDto.",
                ex
            )
    }

let mapCredentialDomainTypeDtosToCredentialUseCaseTypeDtos
  (handleError: HandleErrorBuilder)
  (domainDtos: CredentialDomainTypeDto list)
  : Result<CredentialUseCaseTypeDto list, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.UseCases.UseCaseTypeMappers.mapExistingCredentialUseCaseTypeDtoToCredentialDomainTypeDtos
    handleError {
        try
            return domainDtos
            |> List.map (fun dto ->
                {
                    Id = dto.Id
                    InfrastructureType = dto.InfrastructureType
                    Credentials = dto.Credentials
                    Username = dto.ExternalUsername
                }
            )
        with ex ->
            return! MyDogsbodyException(
                action,
                "Failed to map CredentialDomainTypeDto list to CredentialUseCaseTypeDto list.",
                ex
            )
    }