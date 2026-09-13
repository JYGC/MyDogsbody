module MyDogsbody.Domain.Calendar.ListGoogleAccountsWorkflow

open MyDogsbody.Domain.Calendar

/// Lists every registered Google account, ordered by email so the table has a stable order.
let listGoogleAccounts
    (listGoogleAccounts: ListGoogleAccounts)
    ()
    : Result<RegisteredGoogleAccount list, CalendarError> =
    listGoogleAccounts ()
    |> Result.map (List.sortBy (fun account -> GoogleEmail.value account.EmailAddress))
