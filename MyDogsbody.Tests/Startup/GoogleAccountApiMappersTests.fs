module MyDogsbody.Tests.Startup.GoogleAccountApiMappersTests

open System
open Xunit
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Startup

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private anAction = ActionNames.MyDogsbody.Startup.GoogleAccountApi.getAccounts
let private accountId value = GoogleAccountId.create value |> valueOrFail
let private email value = GoogleEmail.create value |> valueOrFail
let private calendarId value = CalendarId.create value |> valueOrFail

// ---------- domain -> UI record ----------

[<Fact; Trait("Level", "Unit")>]
let ``toCalendarUiType carries every field`` () =
    let calendar: AvailableCalendar =
        { Id = calendarId "cal-1"; Name = CalendarName.create "Invoices" |> valueOrFail; IsPrimary = true }

    let actual = GoogleAccountApiMappers.toCalendarUiType calendar

    Assert.Equal("cal-1", actual.Id)
    Assert.Equal("Invoices", actual.Name)
    Assert.True actual.IsPrimary

[<Fact; Trait("Level", "Unit")>]
let ``toGoogleAccountUiType carries every field, with a chosen default calendar`` () =
    let account: RegisteredGoogleAccount =
        {
            Id = accountId "acc-1"
            EmailAddress = email "person@gmail.com"
            DefaultInvoiceCalendar = Some(calendarId "cal-1")
            NeedsReauthorisation = true
        }

    let actual = GoogleAccountApiMappers.toGoogleAccountUiType account

    Assert.Equal("acc-1", actual.Id)
    Assert.Equal("person@gmail.com", actual.EmailAddress)
    Assert.Equal(Some "cal-1", actual.DefaultInvoiceCalendarId)
    Assert.True actual.NeedsReauthorisation

[<Fact; Trait("Level", "Unit")>]
let ``toGoogleAccountUiType maps no default calendar to None, not a guess`` () =
    let account: RegisteredGoogleAccount =
        {
            Id = accountId "acc-1"
            EmailAddress = email "person@gmail.com"
            DefaultInvoiceCalendar = None
            NeedsReauthorisation = false
        }

    let actual = GoogleAccountApiMappers.toGoogleAccountUiType account

    Assert.Equal(None, actual.DefaultInvoiceCalendarId)
    Assert.False actual.NeedsReauthorisation

// ---------- inbound: adapter exception -> CalendarError ----------

let private authoriseAction = ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise
let private listCalendarsAction = ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars

[<Theory; Trait("Level", "Contract")>]
[<InlineData("The consent flow was cancelled or denied.")>]
let ``toAuthorisationError maps a cancelled consent flow`` (message: string) =
    let ex = MyDogsbodyException(authoriseAction, message, ApplicationException message)
    Assert.Equal(AuthorisationCancelled, GoogleAccountApiMappers.toAuthorisationError ex)

[<Fact; Trait("Level", "Contract")>]
let ``toAuthorisationError maps a malformed client secret, carrying the message`` () =
    let message = "The stored Google client secret is malformed."
    let ex = MyDogsbodyException(authoriseAction, message, ApplicationException message)

    Assert.Equal(ClientSecretInvalid message, GoogleAccountApiMappers.toAuthorisationError ex)

[<Fact; Trait("Level", "Contract")>]
let ``toAuthorisationError maps an unavailable email`` () =
    let message = "The authorised account's email address could not be read."
    let ex = MyDogsbodyException(authoriseAction, message, ApplicationException message)

    Assert.Equal(AccountEmailUnavailable, GoogleAccountApiMappers.toAuthorisationError ex)

[<Fact; Trait("Level", "Contract")>]
let ``toAuthorisationError keeps the loopback-port sentence rather than the listener exception's`` () =
    // requirements.md: "WHEN the loopback port is already in use THE SYSTEM SHALL report that
    // specifically". GoogleAuthorization chose that sentence deliberately; preferring the inner
    // exception's message here would replace it with HttpListenerException's own text, in which
    // the words "loopback" and "port" never appear.
    let ex =
        MyDogsbodyException(
            authoriseAction,
            "The loopback port is already in use.",
            Net.HttpListenerException(
                183,
                "Failed to listen on prefix because it conflicts with an existing registration on the machine."
            )
        )

    Assert.Equal(AuthorisationFailed "The loopback port is already in use.", GoogleAccountApiMappers.toAuthorisationError ex)

[<Fact; Trait("Level", "Contract")>]
let ``toAuthorisationError keeps the timed-out sentence rather than the cancellation's bare message`` () =
    // requirements.md: "WHEN the user closes the browser without completing consent THE SYSTEM
    // SHALL time out with a reason". The inner exception is not a reason - it says only "The
    // operation was canceled." (or "A task was canceled." when the SDK's cancellation surfaces as
    // a TaskCanceledException), which tells the user nothing about what was cancelled or why.
    let operationCancelled =
        MyDogsbodyException(authoriseAction, "The consent flow timed out.", OperationCanceledException())

    let taskCancelled =
        MyDogsbodyException(authoriseAction, "The consent flow timed out.", Threading.Tasks.TaskCanceledException())

    Assert.Equal(AuthorisationFailed "The consent flow timed out.", GoogleAccountApiMappers.toAuthorisationError operationCancelled)
    Assert.Equal(AuthorisationFailed "The consent flow timed out.", GoogleAccountApiMappers.toAuthorisationError taskCancelled)

[<Fact; Trait("Level", "Contract")>]
let ``the message a user is shown for the two named authorisation failures is the one the adapter chose`` () =
    // The whole inbound-then-outbound translation, which is what actually reaches the MudAlert.
    let userSees (adapterMessage: string) (inner: exn) =
        MyDogsbodyException(authoriseAction, adapterMessage, inner)
        |> GoogleAccountApiMappers.toAuthorisationError
        |> GoogleAccountApiMappers.toMyDogsbodyException anAction
        |> fun ex -> ex.Message

    Assert.Equal(
        "The loopback port is already in use.",
        userSees "The loopback port is already in use." (Net.HttpListenerException(183, "conflicts with an existing registration"))
    )

    Assert.Equal("The consent flow timed out.", userSees "The consent flow timed out." (OperationCanceledException()))

[<Fact; Trait("Level", "Contract")>]
let ``toAuthorisationError keeps the calendar-access-not-granted sentence, which carries the remedy`` () =
    // A consent that completed without the calendar scope (Google's granular consent screen, box
    // left unticked). The adapter's sentence says what to do about it; the inner exception only
    // lists the scopes that were granted, which is diagnostics, not an instruction.
    let sentence =
        "Google Calendar access was not granted - tick the calendar permission on Google's consent screen and try again."

    let ex =
        MyDogsbodyException(
            authoriseAction,
            sentence,
            ApplicationException "Granted scopes: https://www.googleapis.com/auth/userinfo.email openid"
        )

    Assert.Equal(AuthorisationFailed sentence, GoogleAccountApiMappers.toAuthorisationError ex)

    // And the whole inbound-then-outbound chain, which is what the MudAlert renders.
    let userSees =
        ex
        |> GoogleAccountApiMappers.toAuthorisationError
        |> GoogleAccountApiMappers.toMyDogsbodyException anAction

    Assert.Equal(sentence, userSees.Message)
    Assert.Equal(anAction, userSees.ActionName)

[<Fact; Trait("Level", "Contract")>]
let ``toAuthorisationError maps anything else to AuthorisationFailed, preferring the inner exception's message`` () =
    // Unchanged: "Authorisation failed." is the adapter's catch-all and carries nothing, so the
    // inner exception is the only place the real reason lives.
    let ex = MyDogsbodyException(authoriseAction, "Authorisation failed.", InvalidOperationException "the real reason")

    Assert.Equal(AuthorisationFailed "the real reason", GoogleAccountApiMappers.toAuthorisationError ex)

[<Fact; Trait("Level", "Contract")>]
let ``toAuthorisationError falls back to the exception's own message when there is no inner exception`` () =
    let ex = MyDogsbodyException(authoriseAction, "Authorisation failed.")

    Assert.Equal(AuthorisationFailed "Authorisation failed.", GoogleAccountApiMappers.toAuthorisationError ex)

[<Fact; Trait("Level", "Contract")>]
let ``toListCalendarsError maps a 401/403-shaped message to NotAuthorised, carrying the account id`` () =
    let id = accountId "acc-1"
    let ex = MyDogsbodyException(listCalendarsAction, "The stored Google credential is no longer authorised.")

    Assert.Equal(NotAuthorised id, GoogleAccountApiMappers.toListCalendarsError id ex)

[<Fact; Trait("Level", "Contract")>]
let ``toListCalendarsError maps a missing stored credential to NotAuthorised too`` () =
    let id = accountId "acc-1"
    let ex = MyDogsbodyException(authoriseAction, "No stored credential for this account.")

    Assert.Equal(NotAuthorised id, GoogleAccountApiMappers.toListCalendarsError id ex)

[<Fact; Trait("Level", "Contract")>]
let ``toListCalendarsError maps a 429-shaped message to CalendarRateLimited, distinct from NotAuthorised`` () =
    let id = accountId "acc-1"
    let message = "Google is rate-limiting this account; try again shortly."
    let ex = MyDogsbodyException(listCalendarsAction, message)

    Assert.Equal(CalendarRateLimited message, GoogleAccountApiMappers.toListCalendarsError id ex)

[<Fact; Trait("Level", "Contract")>]
let ``toListCalendarsError maps anything else to CalendarUnreachable`` () =
    let id = accountId "acc-1"
    let message = "Could not reach Google Calendar."
    let ex = MyDogsbodyException(listCalendarsAction, message)

    Assert.Equal(CalendarUnreachable message, GoogleAccountApiMappers.toListCalendarsError id ex)

[<Fact; Trait("Level", "Contract")>]
let ``toListCalendarsError maps the API-not-enabled 403 to its own case, NOT to NotAuthorised`` () =
    // Found in real use: a Cloud project without the Calendar API switched on returns a bare 403,
    // which used to read as "re-authorise this account" - advice that can never fix it, so the
    // user loops. The case is distinct and the payload keeps Google's own sentence, which names
    // the project and the URL that enables the API.
    let id = accountId "acc-1"

    let message =
        "The Google Calendar API is not enabled for this project. Google Calendar API has not been used in project 000000000000 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/calendar-json.googleapis.com/overview?project=000000000000 then retry."

    let actual = GoogleAccountApiMappers.toListCalendarsError id (MyDogsbodyException(listCalendarsAction, message))

    Assert.Equal(CalendarApiNotEnabled message, actual)
    Assert.NotEqual(NotAuthorised id, actual)

[<Fact; Trait("Level", "Contract")>]
let ``toListCalendarsError maps a malformed stored client secret to ClientSecretInvalid, NOT CalendarUnreachable`` () =
    // `loadCredential` parses the stored client secret before it ever reaches Google, so this
    // failure is not "Google could not be reached" - nothing was sent. Reporting it as
    // CalendarUnreachable both misnames it and logs it, when the user simply needs to re-paste.
    let id = accountId "acc-1"
    let message = "The stored Google client secret is malformed."

    let actual = GoogleAccountApiMappers.toListCalendarsError id (MyDogsbodyException(authoriseAction, message))

    Assert.Equal(ClientSecretInvalid message, actual)
    Assert.NotEqual(CalendarUnreachable message, actual)

[<Fact; Trait("Level", "Contract")>]
let ``the message a user is shown for a malformed stored client secret names the secret`` () =
    // The whole inbound-then-outbound translation, ending at the string the MudAlert renders.
    let id = accountId "acc-1"

    let shown =
        MyDogsbodyException(
            authoriseAction,
            "The stored Google client secret is malformed.",
            InvalidOperationException "Error deserializing JSON credential data."
        )
        |> GoogleAccountApiMappers.toListCalendarsError id
        |> GoogleAccountApiMappers.toMyDogsbodyException listCalendarsAction

    Assert.Equal("The stored Google client secret is malformed.", shown.Message)
    // Expected, so it passes through handleError unlogged - the ApplicationException marker.
    Assert.IsType<ApplicationException>(shown.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``toStoreError wraps any store failure as GoogleStoreFailed carrying the message`` () =
    let ex =
        MyDogsbodyException(
            ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.getAll,
            "Failed to retrieve all Google accounts.",
            InvalidOperationException "disk gone"
        )

    Assert.Equal(GoogleStoreFailed "Failed to retrieve all Google accounts.", GoogleAccountApiMappers.toStoreError ex)

// ---------- outbound: CalendarError -> MyDogsbodyException ----------

[<Fact; Trait("Level", "Contract")>]
let ``ClientSecretMissing becomes an unlogged exception`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction ClientSecretMissing

    Assert.Equal(anAction, actual.ActionName)
    Assert.False(String.IsNullOrWhiteSpace actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``ClientSecretInvalid becomes an unlogged exception carrying the reason`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction (ClientSecretInvalid "bad json")

    Assert.Equal("bad json", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``AuthorisationCancelled becomes an unlogged exception`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction AuthorisationCancelled

    Assert.False(String.IsNullOrWhiteSpace actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``AccountAlreadyRegistered becomes an unlogged exception naming the email`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction (AccountAlreadyRegistered(email "dup@gmail.com"))

    Assert.Contains("dup@gmail.com", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``AccountNotRegistered becomes an unlogged exception naming the id`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction (AccountNotRegistered(accountId "acc-1"))

    Assert.Contains("acc-1", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``AccountEmailUnavailable becomes an unlogged exception`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction AccountEmailUnavailable

    Assert.False(String.IsNullOrWhiteSpace actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``NotAuthorised becomes an unlogged exception naming the id`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction (NotAuthorised(accountId "acc-1"))

    Assert.Contains("acc-1", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``NoDefaultCalendar becomes an unlogged exception naming the id`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction (NoDefaultCalendar(accountId "acc-1"))

    Assert.Contains("acc-1", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``CalendarNoLongerExists becomes an unlogged exception naming the calendar`` () =
    let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction (CalendarNoLongerExists(calendarId "cal-1"))

    Assert.Contains("cal-1", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``GoogleAccountIdInvalid and CalendarIdInvalid become unlogged exceptions carrying their reasons`` () =
    let accountActual = GoogleAccountApiMappers.toMyDogsbodyException anAction (GoogleAccountIdInvalid "bad id")
    let calendarActual = GoogleAccountApiMappers.toMyDogsbodyException anAction (CalendarIdInvalid "bad cal id")

    Assert.Equal("bad id", accountActual.Message)
    Assert.IsType<ApplicationException>(accountActual.InnerException) |> ignore
    Assert.Equal("bad cal id", calendarActual.Message)
    Assert.IsType<ApplicationException>(calendarActual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``AuthorisationFailed, CalendarUnreachable, CalendarRateLimited and GoogleStoreFailed carry their message and are left unmarked`` () =
    let cases: (CalendarError * string) list =
        [
            AuthorisationFailed "auth reason", "auth reason"
            CalendarUnreachable "unreachable reason", "unreachable reason"
            CalendarRateLimited "rate limit reason", "rate limit reason"
            GoogleStoreFailed "store reason", "store reason"
        ]

    for error, expectedMessage in cases do
        let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction error
        Assert.Equal(anAction, actual.ActionName)
        Assert.Equal(expectedMessage, actual.Message)
        Assert.Null actual.InnerException

[<Fact; Trait("Level", "Contract")>]
let ``every CalendarError case produces a non-empty message and the declared action`` () =
    let id = accountId "acc-1"
    let cal = calendarId "cal-1"

    let allCases: CalendarError list =
        [
            ClientSecretMissing
            ClientSecretInvalid "a"
            AuthorisationCancelled
            AuthorisationFailed "b"
            AccountAlreadyRegistered(email "c@example.com")
            AccountNotRegistered id
            AccountEmailUnavailable
            NotAuthorised id
            CalendarApiNotEnabled "c2"
            CalendarUnreachable "d"
            CalendarRateLimited "e"
            CalendarNoLongerExists cal
            NoDefaultCalendar id
            GoogleStoreFailed "f"
            GoogleAccountIdInvalid "g"
            CalendarIdInvalid "h"
        ]

    let declaredCases = Reflection.FSharpType.GetUnionCases(typeof<CalendarError>) |> Array.length
    Assert.Equal(declaredCases, List.length allCases)

    for case in allCases do
        let actual = GoogleAccountApiMappers.toMyDogsbodyException anAction case
        Assert.False(String.IsNullOrWhiteSpace actual.Message, $"{case} produced an empty message")
        Assert.Equal(anAction, actual.ActionName)
