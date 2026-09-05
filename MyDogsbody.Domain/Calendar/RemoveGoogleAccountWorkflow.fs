module MyDogsbody.Domain.Calendar.RemoveGoogleAccountWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Calendar

/// Removes a Google account's local record and token.
///
/// Never attempts to revoke access at Google (Q3.6) - that happens, if at all, at Google's own
/// site, on the user's own initiative.
let removeGoogleAccount
    (removeGoogleAccount: RemoveGoogleAccount)
    (accountId: string)
    : Result<unit, CalendarError> =
    result {
        let! accountId = GoogleAccountId.create accountId |> Result.mapError GoogleAccountIdInvalid
        let! found = removeGoogleAccount accountId

        if not found then
            return! Error (AccountNotRegistered accountId)
        else
            return ()
    }
