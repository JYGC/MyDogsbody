module MyDogsbody.Spine.UseCases.CredentialUseCases

open MyDogsbody.Integrations.Credentials.Database.Types
open MyDogsbody.Spine.UseCases.Types
open MyDogsbody.Exceptions.Types
open MyDogsbody.Builders
open MyDogsbody.Spine.Domains

let addNewCredential
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  (credentialsUseCaseTypeDto: AddCredentialUseCaseTypeDto)
  : Result<unit, MyDogsbodyException> =
    credentialsUseCaseTypeDto
    |> UseCaseTypeMappers.mapAddCredentialUseCaseTypeDtoToAddCredentialDomainTypeDto
        handleError
    |> Result.bind (
        CredentialsDomain.addNewCredential
            handleError
            getCredentialCollection
    )

let editCredential
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  (credentialsUseCaseTypeDto: CredentialUseCaseTypeDto)
  : Result<unit, MyDogsbodyException> =
    credentialsUseCaseTypeDto
    |> UseCaseTypeMappers.mapCredentialUseCaseTypeDtoToCredentialDomainTypeDto
        handleError
    |> Result.bind (
        CredentialsDomain.editCredential
            handleError
            getCredentialCollection
    )

let getAllCredentials
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  : Result<CredentialUseCaseTypeDto list, MyDogsbodyException> =
    CredentialsDomain.getAllCredentials
        handleError
        getCredentialCollection
    |> Result.bind (
        UseCaseTypeMappers.mapCredentialDomainTypeDtosToCredentialUseCaseTypeDtos
            handleError
    )
