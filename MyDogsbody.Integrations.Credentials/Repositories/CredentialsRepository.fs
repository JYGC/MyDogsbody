module MyDogsbody.Integrations.Credentials.Repositories.CredentialsRepository

open MyDogsbody.Integrations.Credentials.Database.Models
open MyDogsbody.Integrations.Credentials.Database.Types
open MyDogsbody.Integrations.Credentials.Repositories.Types
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types

let insertOne
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  (credentialDto: NewCredentialRepositoryTypeDto)
  : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.Repositories.insertOne
    handleError{
        try
            Credential(
                InfrastructureType = credentialDto.InfrastructureType,
                Credentials = credentialDto.Credentials,
                ExternalUsername = credentialDto.ExternalUsername
            )
            |> getCredentialCollection().Insert
            |> ignore
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to insert new credential.",
                ex
            )
    }

let updateOne
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  (credential: ExistingCredentialRepositoryTypeDto)
  : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.Repositories.updateOne
    handleError {
        try
            let existingCredential =
                getCredentialCollection()
                    .Query()
                    .Where(fun ic -> ic.InfrastructureType = credential.InfrastructureType)
                    .FirstOrDefault()
    
            if not (isNull existingCredential) then
                existingCredential.Credentials <- credential.Credentials
                existingCredential.ExternalUsername <- credential.ExternalUsername
                getCredentialCollection().Update existingCredential |> ignore
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to update existing credential.",
                ex
            )
    }

let getAll
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  : Result<ExistingCredentialRepositoryTypeDto list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.Repositories.getAll
    handleError {
        try
            return getCredentialCollection()
                .Query()
                .ToEnumerable()
            |> Seq.map (fun ic ->
                {
                    Id = ic.Id;
                    InfrastructureType = ic.InfrastructureType;
                    Credentials = ic.Credentials;
                    ExternalUsername = ic.ExternalUsername;
                }
            )
            |> Seq.toList
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to retrieve all credentials.",
                ex
            )
    }