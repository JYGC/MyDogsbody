module MyDogsbody.Domain.MailAccounts.SetProfileRootWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

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
