module MyDogsbody.Domain.Calendar.SetDefaultInvoiceCalendarWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Calendar

/// Chooses an account's default invoice calendar.
///
/// Confirms the calendar still exists at Google BEFORE storing it (design decision 6) - so
/// change #7's sync never discovers a dead calendar id halfway through a batch. Neither failure
/// reaches `saveGoogleAccount`.
let setDefaultInvoiceCalendar
    (listGoogleAccounts: ListGoogleAccounts)
    (listCalendars: ListCalendars)
    (saveGoogleAccount: SaveGoogleAccount)
    (accountId: string)
    (calendarId: string)
    : Result<RegisteredGoogleAccount, CalendarError> =
    result {
        let! accountId = GoogleAccountId.create accountId |> Result.mapError GoogleAccountIdInvalid
        let! calendarId = CalendarId.create calendarId |> Result.mapError CalendarIdInvalid

        let! accounts = listGoogleAccounts ()

        let! account =
            match accounts |> List.tryFind (fun a -> a.Id = accountId) with
            | Some account -> Ok account
            | None -> Error (AccountNotRegistered accountId)

        let! calendars = listCalendars accountId

        let calendarExists = calendars |> List.exists (fun c -> c.Id = calendarId)

        if not calendarExists then
            return! Error (CalendarNoLongerExists calendarId)
        else
            return! saveGoogleAccount { account with DefaultInvoiceCalendar = Some calendarId }
    }
