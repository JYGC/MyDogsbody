module MyDogsbody.Domain.Calendar.RegisterGoogleAccountWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Calendar

/// Registers a Google account.
///
/// Refuses before opening the browser when the registration cannot possibly succeed regardless
/// of which account is chosen - no client secret has been supplied - because completing consent
/// only to be refused afterwards wastes the user's time and leaves a granted scope with nothing
/// to show for it.
///
/// `AccountAlreadyRegistered` cannot be checked before that point: which account is a duplicate
/// is only known once `authoriseAccount` has returned an email, so that check runs immediately
/// afterwards, before anything is saved. This is a deliberate reading of design.md's sequence
/// diagram, which shows the check ahead of the browser step for narrative grouping rather than
/// as an achievable call order - a browser-issued email cannot be compared before the browser
/// step has produced one.
///
/// That ordering is what `discardAuthorisation` pays for. Consent has already persisted a token
/// against the id `authoriseAccount` returns by the time the duplicate is found, so refusing by
/// simply not saving would leave that token behind with no account row pointing at it - and the
/// only thing that deletes a token is removing the account it belongs to. Every refused
/// duplicate would strand another one.
///
/// The duplicate check is only the *first* step that can fail after consent, not the only one:
/// reading the existing accounts and saving the new one can both fail too, and each strands the
/// same token for the same reason. So the discard covers the whole post-consent tail rather than
/// one branch of it - anything that leaves without an account row hands the token back.
///
/// The one post-consent step this workflow cannot see is inside `authoriseAccount` itself:
/// reading the account's email once consent has already written the token. An `Error` from it
/// carries no id to discard, so that step is the adapter's to clean up - see `AuthoriseAccount`.
let registerGoogleAccount
    (loadClientSecret: LoadClientSecret)
    (listGoogleAccounts: ListGoogleAccounts)
    (authoriseAccount: AuthoriseAccount)
    (discardAuthorisation: DiscardAuthorisation)
    (saveGoogleAccount: SaveGoogleAccount)
    ()
    : Result<RegisteredGoogleAccount, CalendarError> =
    result {
        let! secret = loadClientSecret ()

        do!
            match secret with
            | Some _ -> Ok ()
            | None -> Error ClientSecretMissing

        let! (email, accountId) = authoriseAccount ()

        // Everything below this line runs with a token already persisted against `accountId`.
        // Grouped so that every way of leaving without an account row - a duplicate, an
        // unreadable account list, a refused save - goes through the one discard, instead of
        // only the duplicate that first made the leak visible.
        let registered =
            result {
                let! existing = listGoogleAccounts ()

                let alreadyRegistered =
                    existing |> List.exists (fun account -> account.EmailAddress = email)

                if alreadyRegistered then
                    return! Error (AccountAlreadyRegistered email)
                else
                    // A store reporting Error means the account was not saved: that is the
                    // contract `SaveGoogleAccount` declares, and it is what makes discarding
                    // safe here rather than a guess about how far the write got.
                    return!
                        saveGoogleAccount
                            {
                                Id = accountId
                                EmailAddress = email
                                DefaultInvoiceCalendar = None
                                NeedsReauthorisation = false
                            }
            }

        match registered with
        | Ok account -> return account
        | Error error ->
            // Deliberately discarded rather than bound: a failure to clean up must not replace
            // the answer the user actually needs, which is why the registration was refused.
            // The discard adapter's own handleError has already recorded it.
            discardAuthorisation accountId |> ignore

            return! Error error
    }
