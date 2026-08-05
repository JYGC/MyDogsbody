/// The credentials adapter: the functions that satisfy the domain's LoadCredentials,
/// SaveCredential and UpdateCredential dependency types.
///
/// Outer ring, so the shape is the established one - dependencies first, input last,
/// Result<'T, MyDogsbodyException> out, written with handleError. The domain error types are
/// not named here; translating between the two is the composition root's job and happens in
/// CredentialApiFactory, nowhere else.
module MyDogsbody.Integrations.Credentials.CredentialStore

open System
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Credentials
open MyDogsbody.Integrations.Credentials.Database.Types

/// A row that cannot be mapped back is a data-integrity failure, not something a user did, so
/// it is raised and caught like any other unexpected failure rather than returned as a value.
let private mapOrRaise (entity: Database.Models.Credential) =
    match CredentialEntityMappers.toStoredCredential entity with
    | Ok storedCredential -> storedCredential
    | Error reason -> raise (InvalidOperationException $"Stored credential is unusable: {reason}")

let getAll
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> CredentialsCollection)
    ()
    : Result<StoredCredential list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.getAll

    handleError {
        try
            return
                getCredentialCollection()
                    .Query()
                    .ToEnumerable()
                |> Seq.map mapOrRaise
                |> Seq.toList
        with ex ->
            return! MyDogsbodyException(action, "Failed to retrieve all credentials.", ex)
    }

let insertOne
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> CredentialsCollection)
    (credential: ValidCredential)
    : Result<StoredCredential, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.insertOne

    handleError {
        try
            let entity = CredentialEntityMappers.toNewEntity credential
            getCredentialCollection().Insert entity |> ignore

            // Insert stamps the entity's Id, so the identifier the caller gets back is the one
            // the store actually assigned rather than one guessed here.
            return mapOrRaise entity
        with ex ->
            return! MyDogsbodyException(action, "Failed to insert new credential.", ex)
    }

/// Ok None means no row carried that identifier. Reporting it rather than silently succeeding is
/// what lets EditCredentialWorkflow decide that absence is CredentialNotFound.
let updateOne
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> CredentialsCollection)
    (edit: ValidCredentialEdit)
    : Result<StoredCredential option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.updateOne

    handleError {
        try
            let collection = getCredentialCollection()
            let objectId = CredentialEntityMappers.toObjectId edit.Id

            // Addressed by identifier. Matching on InfrastructureType is what used to update
            // the wrong row when two credentials shared a type.
            let existing = collection.FindById objectId

            if isNull (box existing) then
                return None
            else
                let updated = CredentialEntityMappers.applyEdit edit existing
                collection.Update updated |> ignore
                return Some (mapOrRaise updated)
        with ex ->
            return! MyDogsbodyException(action, "Failed to update existing credential.", ex)
    }
