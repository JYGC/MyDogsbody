module MyDogsbody.Domain.Credentials.AddCredentialWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Credentials

/// Turns what the dialog produced into something the store can be handed.
///
/// The order of the two checks is the order the user is told about them: the secret first, then
/// the username. Private because it is a step, not a use case.
let private validate (input: UnvalidatedCredential) : Result<ValidCredential, CredentialError> =
    result {
        let! credentials =
            CredentialSecret.create input.Credentials
            |> Result.mapError CredentialsInvalid

        let! externalUsername =
            ExternalUsername.create input.ExternalUsername
            |> Result.mapError ExternalUsernameInvalid

        return
            {
                Infrastructure = input.Infrastructure
                Credentials = credentials
                ExternalUsername = externalUsername
            }
    }

/// Adds a new credential.
///
/// Dependencies first, input last, Result out. saveCredential performing a database write is
/// invisible here on purpose - this file sees a function value, which is why the whole workflow
/// tests with a lambda.
let addCredential
    (saveCredential: SaveCredential)
    (input: UnvalidatedCredential)
    : Result<StoredCredential, CredentialError> =
    result {
        let! validCredential = validate input
        return! saveCredential validCredential
    }
