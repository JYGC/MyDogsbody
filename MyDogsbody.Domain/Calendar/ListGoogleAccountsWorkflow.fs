module MyDogsbody.Domain.Calendar.ListGoogleAccountsWorkflow

open MyDogsbody.Domain.Calendar

/// So the table has a stable order.
let listGoogleAccounts
    (listGoogleAccounts: ListGoogleAccounts)
    ()
    : Result<RegisteredGoogleAccount list, CalendarError> =
    let orderedByEmail = List.sortBy (fun account -> GoogleEmail.value account.EmailAddress)

    listGoogleAccounts () |> Result.map orderedByEmail
