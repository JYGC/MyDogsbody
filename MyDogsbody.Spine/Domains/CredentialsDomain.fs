module MyDogsbody.Spine.Domains.CredentialsDomain

open MyDogsbody.Builders
open MyDogsbody.Spine.Domains.Types
open MyDogsbody.Integrations.Credentials.Database.Types
open MyDogsbody.Integrations.Credentials.UseCases
open MyDogsbody.Exceptions.Types

let addNewCredential
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  (credentialsUseCaseTypeDto: AddCredentialDomainTypeDto)
  : Result<unit, MyDogsbodyException> =
    credentialsUseCaseTypeDto
    |> DomianTypeMappers.mapAddCredentialDomainTypeDtoToNewCredentialUseCaseTypeDto handleError
    |> Result.bind (
        CredentialsUseCases.insertOne
            handleError
            getCredentialCollection
    )

let editCredential
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  (credentialsUseCaseTypeDto: CredentialDomainTypeDto)
  : Result<unit, MyDogsbodyException> =
    credentialsUseCaseTypeDto
    |> DomianTypeMappers.mapCredentialDomainTypeDtoToExistingCredentialUseCaseTypeDto handleError
    |> Result.bind (
        CredentialsUseCases.updateOne
            handleError
            getCredentialCollection
    )

let getAllCredentials
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  : Result<CredentialDomainTypeDto list, MyDogsbodyException> =
    CredentialsUseCases.getAll
        handleError
        getCredentialCollection
    |> Result.bind (
        DomianTypeMappers.mapExistingCredentialUseCaseTypeDtoToCredentialDomainTypeDtos
            handleError
    )