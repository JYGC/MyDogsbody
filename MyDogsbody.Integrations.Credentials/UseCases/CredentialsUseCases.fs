module MyDogsbody.Integrations.Credentials.UseCases.CredentialsUseCases

open MyDogsbody.Builders
open MyDogsbody.Integrations.Credentials.Database.Types
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Credentials.UseCases.Types
open MyDogsbody.Integrations.Credentials.Repositories

let insertOne
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  (credentialUseCaseTypeDto: NewCredentialUseCaseTypeDto)
  : Result<unit, MyDogsbodyException> =
    credentialUseCaseTypeDto
    |> CredentialsUseCaseTypeMappers.mapNewCredentialUseCaseTypeDtoToNewCredentialRepositoryTypeDto handleError
    |> Result.bind (CredentialsRepository.insertOne handleError getCredentialCollection)

let updateOne
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  (credentialUseCaseTypeDto: ExistingCredentialUseCaseTypeDto)
  : Result<unit, MyDogsbodyException> =
    credentialUseCaseTypeDto
    |> CredentialsUseCaseTypeMappers.mapExistingCredentialUseCaseTypeDtoToExistingCredentialRepositoryTypeDto handleError
    |> Result.bind (CredentialsRepository.updateOne handleError getCredentialCollection)

let getAll
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  : Result<ExistingCredentialUseCaseTypeDto list, MyDogsbodyException> =
    CredentialsRepository.getAll handleError getCredentialCollection
    |> Result.bind (
        CredentialsUseCaseTypeMappers.mapExistingCredentialRepositoryTypeDtosToExistingCredentialUseCaseTypeDtos
            handleError
    )
