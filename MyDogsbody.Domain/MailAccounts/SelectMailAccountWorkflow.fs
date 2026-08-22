module MyDogsbody.Domain.MailAccounts.SelectMailAccountWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

/// Selects an account for import. Refuses an id that does not name a stored account, and never
/// reaches the store when it does.
let selectMailAccount
    (loadMailAccounts: LoadMailAccounts)
    (saveSelectedMailAccount: SaveSelectedMailAccount)
    (input: string)
    : Result<MailAccountId, MailAccountError> =
    result {
        let! id = MailAccountId.create input |> Result.mapError MailAccountIdInvalid
        let! accounts = loadMailAccounts ()

        let known = accounts |> List.exists (fun account -> account.Id = id)

        if not known then
            return! Error (MailAccountNotFound id)
        else
            do! saveSelectedMailAccount (Some id)
            return id
    }
