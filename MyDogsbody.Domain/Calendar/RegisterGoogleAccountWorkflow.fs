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
let registerGoogleAccount
    (loadClientSecret: LoadClientSecret)
    (listGoogleAccounts: ListGoogleAccounts)
    (authoriseAccount: AuthoriseAccount)
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
        let! existing = listGoogleAccounts ()

        let alreadyRegistered =
            existing |> List.exists (fun account -> account.EmailAddress = email)

        if alreadyRegistered then
            return! Error (AccountAlreadyRegistered email)
        else
            return!
                saveGoogleAccount
                    {
                        Id = accountId
                        EmailAddress = email
                        DefaultInvoiceCalendar = None
                        NeedsReauthorisation = false
                    }
    }
