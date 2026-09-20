module MyDogsbody.Domain.MailAccounts.SelectMailAccountWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

let selectMailAccount
    (loadMailAccounts: LoadMailAccounts)
    (saveSelectedMailAccount: SaveSelectedMailAccount)
    (input: string)
    : Result<MailAccountId, MailAccountError> =
    result {
        let! id = MailAccountId.create input |> Result.mapError MailAccountIdInvalid
        let! accounts = loadMailAccounts ()

        let theIdNamesAStoredAccount = accounts |> List.exists (fun account -> account.Id = id)

        if not theIdNamesAStoredAccount then
            return! Error (MailAccountNotFound id)
        else
            do! saveSelectedMailAccount (Some id)
            return id
    }
