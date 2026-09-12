module MyDogsbody.Domain.Calendar.ReauthoriseGoogleAccountWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Calendar

/// Re-authorises an account whose token has expired or been revoked.
///
/// Keeps the account's chosen default calendar, because it is looked up and re-saved via
/// `{ account with ... }` rather than rebuilt from scratch (requirements.md: "an account is
/// re-authorised THE SYSTEM SHALL keep its chosen default calendar").
let reauthoriseGoogleAccount
    (listGoogleAccounts: ListGoogleAccounts)
    (reauthoriseAccount: ReauthoriseAccount)
    (saveGoogleAccount: SaveGoogleAccount)
    (accountId: string)
    : Result<RegisteredGoogleAccount, CalendarError> =
    result {
        let! id = GoogleAccountId.create accountId |> Result.mapError GoogleAccountIdInvalid
        let! accounts = listGoogleAccounts ()

        let! account =
            match accounts |> List.tryFind (fun a -> a.Id = id) with
            | Some account -> Ok account
            | None -> Error(AccountNotRegistered id)

        let! email = reauthoriseAccount id

        return! saveGoogleAccount { account with EmailAddress = email; NeedsReauthorisation = false }
    }
