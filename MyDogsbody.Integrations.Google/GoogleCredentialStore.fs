/// The Google credential adapter: getAll / insertOne / updateOne over the integration's own
/// LiteDB `Credentials` collection.
///
/// Outer ring, so the shape is the established one - handleError first, the collection getter
/// next, input last, Result<'T, MyDogsbodyException> out. There is no domain error type to
/// translate to and from here: a credential is not a domain concept. In change #6 the Google
/// account factory calls these directly.
module MyDogsbody.Integrations.Google.GoogleCredentialStore

open System
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database.Types

/// A row that cannot be mapped back is a data-integrity failure, not something a user did, so it
/// is raised and caught like any other unexpected failure rather than returned as a value.
let private mapOrRaise (entity: Database.Models.GoogleCredential) =
    match GoogleCredentialEntityMappers.toStoredCredential entity with
    | Ok storedCredential -> storedCredential
    | Error reason -> raise (InvalidOperationException $"Stored credential is unusable: {reason}")

let getAll
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    ()
    : Result<StoredGoogleCredential list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.getAll

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
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (credential: ValidGoogleCredential)
    : Result<StoredGoogleCredential, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.insertOne

    handleError {
        try
            let entity = GoogleCredentialEntityMappers.toNewEntity credential
            getCredentialCollection().Insert entity |> ignore

            // Insert stamps the entity's Id, so the identifier the caller gets back is the one
            // the store actually assigned rather than one guessed here.
            return mapOrRaise entity
        with ex ->
            return! MyDogsbodyException(action, "Failed to insert new credential.", ex)
    }

/// Ok None means no row carried that identifier. Reporting it rather than silently succeeding is
/// what lets the caller decide that absence is "not found".
let updateOne
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (edit: ValidGoogleCredentialEdit)
    : Result<StoredGoogleCredential option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.updateOne

    handleError {
        try
            let collection = getCredentialCollection()
            let objectId = GoogleCredentialEntityMappers.toObjectId edit.Id

            let existing = collection.FindById objectId

            if isNull (box existing) then
                return None
            else
                let updated = GoogleCredentialEntityMappers.applyEdit edit existing
                collection.Update updated |> ignore
                return Some (mapOrRaise updated)
        with ex ->
            return! MyDogsbodyException(action, "Failed to update existing credential.", ex)
    }
