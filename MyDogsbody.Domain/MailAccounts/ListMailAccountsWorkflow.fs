module MyDogsbody.Domain.MailAccounts.ListMailAccountsWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

/// An empty store is not an error - a fresh install has discovered nothing yet.
let listMailAccounts
    (loadMailAccounts: LoadMailAccounts)
    (loadSelectedMailAccount: LoadSelectedMailAccount)
    ()
    : Result<DiscoveredMailAccount list * MailAccountId option, MailAccountError> =
    result {
        let! accounts = loadMailAccounts ()
        let! selected = loadSelectedMailAccount ()
        return accounts, selected
    }
