module MyDogsbody.Domain.Credentials.EditCredentialWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Credentials

let private validate (input: UnvalidatedCredentialEdit) : Result<ValidCredentialEdit, CredentialError> =
    result {
        let! id =
            CredentialId.create input.Id
            |> Result.mapError CredentialIdInvalid

        let! credentials =
            CredentialSecret.create input.Credentials
            |> Result.mapError CredentialsInvalid

        let! externalUsername =
            ExternalUsername.create input.ExternalUsername
            |> Result.mapError ExternalUsernameInvalid

        return
            {
                Id = id
                Infrastructure = input.Infrastructure
                Credentials = credentials
                ExternalUsername = externalUsername
            }
    }

/// The row must already exist. Identified by its id and nothing else - two credentials may
/// share an infrastructure type, and picking by type is what used to update the wrong one.
let private ensureExists
    (stored: StoredCredential list)
    (edit: ValidCredentialEdit)
    : Result<ValidCredentialEdit, CredentialError> =
    if stored |> List.exists (fun credential -> credential.Id = edit.Id) then
        Ok edit
    else
        Error (CredentialNotFound edit.Id)

/// Edits an existing credential.
///
/// Loads before it writes so that "no such credential" is a decision made here, in a function
/// a test can drive with two lambdas, rather than one buried in a query. That is a read then a
/// write with nothing holding them together: for a single-user desktop application the race is
/// accepted, and updateCredential returning None covers the case where the row disappears in
/// between.
let editCredential
    (loadCredentials: LoadCredentials)
    (updateCredential: UpdateCredential)
    (input: UnvalidatedCredentialEdit)
    : Result<StoredCredential, CredentialError> =
    result {
        let! validEdit = validate input
        let! storedCredentials = loadCredentials ()
        let! confirmedEdit = ensureExists storedCredentials validEdit
        let! updated = updateCredential confirmedEdit

        return!
            match updated with
            | Some storedCredential -> Ok storedCredential
            | None -> Error (CredentialNotFound confirmedEdit.Id)
    }
