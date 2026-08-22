module MyDogsbody.Domain.MailAccounts.SetProfileRootWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

/// Validates the chosen folder and persists it. Dependencies first, input last, Result out -
/// saveProfileRoot performing a database write is invisible here on purpose.
let setProfileRoot
    (saveProfileRoot: SaveProfileRoot)
    (input: string)
    : Result<ProfileRootPath, MailAccountError> =
    result {
        let! path = ProfileRootPath.create input |> Result.mapError ProfileRootInvalid
        do! saveProfileRoot path
        return path
    }
