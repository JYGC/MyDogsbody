module MyDogsbody.Tests.Domain.Calendar.ReauthoriseGoogleAccountWorkflowTests

open Xunit
open MyDogsbody.Domain.Calendar

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private accountId value = GoogleAccountId.create value |> valueOrFail
let private email value = GoogleEmail.create value |> valueOrFail
let private calendarId value = CalendarId.create value |> valueOrFail

let private account id emailValue defaultCalendar needsReauth : RegisteredGoogleAccount =
    {
        Id = accountId id
        EmailAddress = email emailValue
        DefaultInvoiceCalendar = defaultCalendar
        NeedsReauthorisation = needsReauth
    }

let private recordingSave () =
    let received = ResizeArray<RegisteredGoogleAccount>()

    let save: SaveGoogleAccount =
        fun account ->
            received.Add account
            Ok account

    save, received

[<Fact; Trait("Level", "Unit")>]
let ``reauthoriseGoogleAccount clears the needs-reauthorisation flag and keeps the chosen default calendar`` () =
    let existing = account "acc-1" "old@example.com" (Some (calendarId "cal-1")) true
    let listAccounts: ListGoogleAccounts = fun () -> Ok [ existing ]
    let reauthorise: ReauthoriseAccount = fun _ -> Ok(email "old@example.com")
    let save, received = recordingSave ()

    let actual = ReauthoriseGoogleAccountWorkflow.reauthoriseGoogleAccount listAccounts reauthorise save "acc-1"

    match actual with
    | Ok updated ->
        Assert.Equal("acc-1", GoogleAccountId.value updated.Id)
        Assert.Equal("old@example.com", GoogleEmail.value updated.EmailAddress)
        Assert.Equal(Some(calendarId "cal-1"), updated.DefaultInvoiceCalendar)
        Assert.False updated.NeedsReauthorisation
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``reauthoriseGoogleAccount updates the email if Google returns a different one`` () =
    let existing = account "acc-1" "old@example.com" None false
    let listAccounts: ListGoogleAccounts = fun () -> Ok [ existing ]
    let reauthorise: ReauthoriseAccount = fun _ -> Ok(email "new@example.com")
    let save, _ = recordingSave ()

    let actual = ReauthoriseGoogleAccountWorkflow.reauthoriseGoogleAccount listAccounts reauthorise save "acc-1"

    match actual with
    | Ok updated -> Assert.Equal("new@example.com", GoogleEmail.value updated.EmailAddress)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``reauthoriseGoogleAccount refuses an unregistered account and never authorises or saves`` () =
    let listAccounts: ListGoogleAccounts = fun () -> Ok []
    let reauthorise: ReauthoriseAccount = fun _ -> failwith "reauthoriseAccount must not be called"
    let save, received = recordingSave ()

    let actual = ReauthoriseGoogleAccountWorkflow.reauthoriseGoogleAccount listAccounts reauthorise save "unknown"

    Assert.Equal(Error(AccountNotRegistered(accountId "unknown")), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``reauthoriseGoogleAccount rejects an empty id and never reaches either dependency`` () =
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let reauthorise: ReauthoriseAccount = fun _ -> failwith "reauthoriseAccount must not be called"
    let save, received = recordingSave ()

    let actual = ReauthoriseGoogleAccountWorkflow.reauthoriseGoogleAccount listAccounts reauthorise save "   "

    Assert.Equal(Error(GoogleAccountIdInvalid "Google account id must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``reauthoriseGoogleAccount propagates a cancelled re-authorisation and never saves`` () =
    let existing = account "acc-1" "old@example.com" None true
    let listAccounts: ListGoogleAccounts = fun () -> Ok [ existing ]
    let reauthorise: ReauthoriseAccount = fun _ -> Error AuthorisationCancelled
    let save, received = recordingSave ()

    let actual = ReauthoriseGoogleAccountWorkflow.reauthoriseGoogleAccount listAccounts reauthorise save "acc-1"

    Assert.Equal(Error AuthorisationCancelled, actual)
    Assert.Empty received
