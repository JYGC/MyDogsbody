module MyDogsbody.Domain.Calendar.RegisterGoogleAccountWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Calendar

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - RegisterGoogleAccountWorkflow.fs: registerGoogleAccount
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
