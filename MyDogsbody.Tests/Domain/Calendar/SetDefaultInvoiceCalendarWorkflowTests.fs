module MyDogsbody.Tests.Domain.Calendar.SetDefaultInvoiceCalendarWorkflowTests

open Xunit
open MyDogsbody.Domain.Calendar

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private accountId value = GoogleAccountId.create value |> valueOrFail
let private calendarId value = CalendarId.create value |> valueOrFail

let private account id : RegisteredGoogleAccount =
    {
        Id = accountId id
        EmailAddress = GoogleEmail.create $"{id}@example.com" |> valueOrFail
        DefaultInvoiceCalendar = None
        NeedsReauthorisation = false
    }

let private calendar id : AvailableCalendar =
    {
        Id = calendarId id
        Name = CalendarName.create $"Calendar {id}" |> valueOrFail
        IsPrimary = false
    }

/// Records every save, so "the store was never reached" is assertable.
let private recordingSave () =
    let received = ResizeArray<RegisteredGoogleAccount>()

    let save: SaveGoogleAccount =
        fun account ->
            received.Add account
            Ok account

    save, received

[<Fact; Trait("Level", "Unit")>]
let ``setDefaultInvoiceCalendar stores the calendar once it is confirmed to exist`` () =
    let existing = account "acc-1"
    let listAccounts: ListGoogleAccounts = fun () -> Ok [ existing ]
    let listCalendars: ListCalendars = fun _ -> Ok [ calendar "cal-1"; calendar "cal-2" ]
    let save, received = recordingSave ()

    let actual =
        SetDefaultInvoiceCalendarWorkflow.setDefaultInvoiceCalendar listAccounts listCalendars save "acc-1" "cal-2"

    match actual with
    | Ok updated ->
        Assert.Equal("acc-1", GoogleAccountId.value updated.Id)
        Assert.Equal(Some (calendarId "cal-2"), updated.DefaultInvoiceCalendar)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``setDefaultInvoiceCalendar refuses an unregistered account and never saves`` () =
    let listAccounts: ListGoogleAccounts = fun () -> Ok [ account "acc-1" ]
    let listCalendars: ListCalendars = fun _ -> failwith "listCalendars must not be called"
    let save, received = recordingSave ()

    let actual =
        SetDefaultInvoiceCalendarWorkflow.setDefaultInvoiceCalendar listAccounts listCalendars save "unknown" "cal-1"

    Assert.Equal(Error (AccountNotRegistered (accountId "unknown")), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``setDefaultInvoiceCalendar refuses a calendar no longer listed, verified before storing, and never saves`` () =
    let listAccounts: ListGoogleAccounts = fun () -> Ok [ account "acc-1" ]
    let listCalendars: ListCalendars = fun _ -> Ok [ calendar "cal-1" ]
    let save, received = recordingSave ()

    let actual =
        SetDefaultInvoiceCalendarWorkflow.setDefaultInvoiceCalendar listAccounts listCalendars save "acc-1" "gone"

    Assert.Equal(Error (CalendarNoLongerExists (calendarId "gone")), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``setDefaultInvoiceCalendar rejects an empty account id and never reaches either dependency`` () =
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let listCalendars: ListCalendars = fun _ -> failwith "listCalendars must not be called"
    let save, received = recordingSave ()

    let actual =
        SetDefaultInvoiceCalendarWorkflow.setDefaultInvoiceCalendar listAccounts listCalendars save "   " "cal-1"

    Assert.Equal(Error (GoogleAccountIdInvalid "Google account id must not be empty."), actual)
    Assert.Empty received
