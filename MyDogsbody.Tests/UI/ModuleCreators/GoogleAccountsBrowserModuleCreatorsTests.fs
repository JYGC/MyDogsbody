module MyDogsbody.Tests.UI.ModuleCreators.GoogleAccountsBrowserModuleCreatorsTests

open System
open Xunit
open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types

/// Runs the work on the calling thread. Production passes an Async.Start equivalent; the seam
/// exists so a test never has to wait on a background thread.
let private runSynchronously (work: unit -> unit) = work ()

let private failure message = MyDogsbodyException("test.action", message, ApplicationException(message))

let private anAccount id defaultCalendarId needsReauth : GoogleAccountUiType =
    {
        Id = id
        EmailAddress = $"{id}@example.com"
        DefaultInvoiceCalendarId = defaultCalendarId
        NeedsReauthorisation = needsReauth
    }

let private aCalendar id name isPrimary : CalendarUiType = { Id = id; Name = name; IsPrimary = isPrimary }

/// A fake `GoogleAccountApi` with defaults that succeed and do nothing, overridable per test.
let private api
    (getClientSecret: unit -> Result<string option, MyDogsbodyException>)
    (setClientSecret: string -> Result<unit, MyDogsbodyException>)
    (getAccounts: unit -> Result<GoogleAccountUiType list, MyDogsbodyException>)
    (registerAccount: unit -> Result<GoogleAccountUiType, MyDogsbodyException>)
    (reauthoriseAccount: string -> Result<GoogleAccountUiType, MyDogsbodyException>)
    (removeAccount: string -> Result<unit, MyDogsbodyException>)
    (getCalendarsFor: string -> Result<CalendarUiType list, MyDogsbodyException>)
    (setDefaultInvoiceCalendar: string -> string -> Result<GoogleAccountUiType, MyDogsbodyException>)
    : GoogleAccountApi =
    {
        GetClientSecret = getClientSecret
        SetClientSecret = setClientSecret
        GetAccounts = getAccounts
        RegisterAccount = registerAccount
        ReauthoriseAccount = reauthoriseAccount
        RemoveAccount = removeAccount
        GetCalendarsFor = getCalendarsFor
        SetDefaultInvoiceCalendar = setDefaultInvoiceCalendar
    }

let private defaultApi () =
    api
        (fun () -> Ok None)
        (fun _ -> Ok())
        (fun () -> Ok [])
        (fun () -> failwith "RegisterAccount must not be called")
        (fun _ -> failwith "ReauthoriseAccount must not be called")
        (fun _ -> Ok())
        (fun _ -> Ok [])
        (fun _ _ -> failwith "SetDefaultInvoiceCalendar must not be called")

[<Fact; Trait("Level", "Unit")>]
let ``the module loads the client secret and the accounts when it is created`` () =
    let googleAccountApi =
        api
            (fun () -> Ok(Some "the-stored-secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" (Some "cal-1") false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    Assert.Equal(Some "the-stored-secret", AVal.force browser.ClientSecretAval)
    Assert.False(AVal.force browser.IsEditingClientSecretAval)
    let accounts = AVal.force browser.AccountsAval
    Assert.Single accounts |> ignore
    Assert.False(AVal.force browser.IsLoadingAval)
    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``a fresh database reports no client secret and no accounts`` () =
    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously (defaultApi ())

    Assert.Equal(None, AVal.force browser.ClientSecretAval)
    Assert.Empty(AVal.force browser.AccountsAval)

[<Fact; Trait("Level", "Unit")>]
let ``a ready account's calendars are loaded automatically, populating its picker`` () =
    let calendarCalls = ResizeArray<string>()

    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" (Some "cal-1") false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun id ->
                calendarCalls.Add id
                Ok [ aCalendar "cal-1" "Invoices" true ])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    Assert.Equal<string list>([ "1" ], List.ofSeq calendarCalls)
    let byAccount = AVal.force browser.CalendarsByAccountIdAval
    Assert.Equal<CalendarUiType list>([ aCalendar "cal-1" "Invoices" true ], byAccount |> Map.find "1")

[<Fact; Trait("Level", "Unit")>]
let ``a failed calendar fetch surfaces the reason instead of silently leaving the picker empty`` () =
    // Real-world bug: the picker had no options and nothing explained why, because a failed
    // GetCalendarsFor was swallowed with `| Error _ -> ()`. The reason (an expired credential, a
    // network failure, a rate limit) must reach ErrorAval so "the picker is empty" is never the
    // whole story.
    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" None false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Error(failure "The account 'acc-1' needs to be re-authorised."))
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    Assert.Equal(Some "The account 'acc-1' needs to be re-authorised.", AVal.force browser.ErrorAval)
    Assert.Empty(AVal.force browser.CalendarsByAccountIdAval)

[<Fact; Trait("Level", "Unit")>]
let ``an account needing re-authorisation never has its calendars fetched`` () =
    let calendarCalls = ResizeArray<string>()

    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" None true ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun id ->
                calendarCalls.Add id
                Ok [])
            (fun _ _ -> failwith "unused")

    GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi
    |> ignore

    Assert.Empty calendarCalls

[<Fact; Trait("Level", "Unit")>]
let ``StartEditingClientSecret opens the field without changing the stored value`` () =
    let googleAccountApi = api (fun () -> Ok(Some "the-stored-secret")) (fun _ -> failwith "unused") (fun () -> Ok []) (fun () -> failwith "unused") (fun _ -> failwith "unused") (fun _ -> Ok()) (fun _ -> Ok []) (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.StartEditingClientSecret()

    Assert.True(AVal.force browser.IsEditingClientSecretAval)
    Assert.Equal(Some "the-stored-secret", AVal.force browser.ClientSecretAval)

[<Fact; Trait("Level", "Unit")>]
let ``CancelEditingClientSecret closes the field without saving anything`` () =
    let saveCalls = ResizeArray<string>()

    let googleAccountApi =
        api
            (fun () -> Ok(Some "the-stored-secret"))
            (fun s ->
                saveCalls.Add s
                Ok())
            (fun () -> Ok [])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.StartEditingClientSecret()
    browser.CancelEditingClientSecret()

    Assert.False(AVal.force browser.IsEditingClientSecretAval)
    Assert.Empty saveCalls
    Assert.Equal(Some "the-stored-secret", AVal.force browser.ClientSecretAval)

[<Fact; Trait("Level", "Unit")>]
let ``SetClientSecret saves the new value, closes the edit field, and reloads`` () =
    let savedSecrets = ResizeArray<string>()
    let stored = ref (Some "old-secret")

    let googleAccountApi =
        api
            (fun () -> Ok stored.Value)
            (fun secret ->
                savedSecrets.Add secret
                stored.Value <- Some secret
                Ok())
            (fun () -> Ok [])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.StartEditingClientSecret()
    browser.SetClientSecret "new-secret"

    Assert.Single savedSecrets |> ignore
    Assert.Equal("new-secret", savedSecrets.[0])
    Assert.Equal(Some "new-secret", AVal.force browser.ClientSecretAval)
    Assert.False(AVal.force browser.IsEditingClientSecretAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed SetClientSecret surfaces the message and leaves the field open for another attempt`` () =
    let googleAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Error(failure "malformed secret"))
            (fun () -> Ok [])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.StartEditingClientSecret()
    browser.SetClientSecret "bad"

    Assert.Equal(Some "malformed secret", AVal.force browser.ErrorAval)
    Assert.Equal(None, AVal.force browser.ClientSecretAval)
    // The field stays open on failure - closing it would silently discard what was typed.
    Assert.True(AVal.force browser.IsEditingClientSecretAval)

[<Fact; Trait("Level", "Unit")>]
let ``RegisterAccount reloads the accounts table on success and clears the registering flag`` () =
    let registered = ref false

    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok(if registered.Value then [ anAccount "1" None false ] else []))
            (fun () ->
                registered.Value <- true
                Ok(anAccount "1" None false))
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    Assert.Empty(AVal.force browser.AccountsAval)

    browser.RegisterAccount()

    Assert.Single(AVal.force browser.AccountsAval) |> ignore
    Assert.False(AVal.force browser.IsRegisteringAval)
    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``RegisterAccount shows it is in progress while the consent flow runs, without blocking the caller`` () =
    // requirements.md: "WHEN a user presses "Add account" THE SYSTEM SHALL start the consent flow and
    // show that it is in progress", and "WHEN authorisation is running THE SYSTEM SHALL NOT block the
    // user interface". Every other RegisterAccount test runs its work on the calling thread, so the
    // flag is only ever read after the flow has finished - which passes whether or not it was ever
    // raised. Here the work is queued, the way Async.Start leaves it, and the flag is read while the
    // consent flow has still to run.
    let queued = Collections.Generic.Queue<unit -> unit>()

    let rec drain () =
        match queued.TryDequeue() with
        | true, work ->
            work ()
            drain ()
        | _ -> ()

    let consentCalls = ref 0

    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok(if consentCalls.Value > 0 then [ anAccount "1" None false ] else []))
            (fun () ->
                consentCalls.Value <- consentCalls.Value + 1
                Ok(anAccount "1" None false))
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [])
            (fun _ _ -> failwith "unused")

    let browser =
        GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule (fun work -> queued.Enqueue work) googleAccountApi

    drain ()
    Assert.False(AVal.force browser.IsRegisteringAval)

    browser.RegisterAccount()

    // RegisterAccount has returned to its caller, the consent flow has not run yet, and the page
    // already says it is in progress.
    Assert.Equal(0, consentCalls.Value)
    Assert.True(AVal.force browser.IsRegisteringAval)

    drain ()

    Assert.Equal(1, consentCalls.Value)
    Assert.False(AVal.force browser.IsRegisteringAval)
    Assert.Single(AVal.force browser.AccountsAval) |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``a failed RegisterAccount surfaces the message and clears the registering flag`` () =
    let googleAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Ok())
            (fun () -> Ok [])
            (fun () -> Error(failure "no client secret has been supplied yet"))
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.RegisterAccount()

    Assert.Equal(Some "no client secret has been supplied yet", AVal.force browser.ErrorAval)
    Assert.False(AVal.force browser.IsRegisteringAval)

[<Fact; Trait("Level", "Unit")>]
let ``ReauthoriseAccount reloads on success, keeping the account's chosen calendar`` () =
    let reauthorised = ref false

    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" (Some "cal-1") (not reauthorised.Value) ])
            (fun () -> failwith "unused")
            (fun id ->
                reauthorised.Value <- true
                Ok(anAccount id (Some "cal-1") false))
            (fun _ -> Ok())
            (fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.ReauthoriseAccount "1"

    let account = AVal.force browser.AccountsAval |> List.exactlyOne
    Assert.False account.NeedsReauthorisation
    Assert.Equal(Some "cal-1", account.DefaultInvoiceCalendarId)
    Assert.False(AVal.force browser.IsRegisteringAval)

[<Fact; Trait("Level", "Unit")>]
let ``RemoveAccount reloads on success and forgets the removed account's calendars`` () =
    let removedIds = ResizeArray<string>()
    let removed = ref false

    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok(if removed.Value then [] else [ anAccount "1" (Some "cal-1") false ]))
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun id ->
                removedIds.Add id
                removed.Value <- true
                Ok())
            (fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    Assert.True(AVal.force browser.CalendarsByAccountIdAval |> Map.containsKey "1")

    browser.RemoveAccount "1"

    Assert.Single removedIds |> ignore
    Assert.Equal("1", removedIds.[0])
    Assert.Empty(AVal.force browser.AccountsAval)
    Assert.False(AVal.force browser.CalendarsByAccountIdAval |> Map.containsKey "1")

[<Fact; Trait("Level", "Unit")>]
let ``a failed RemoveAccount surfaces the message`` () =
    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" None false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Error(failure "no Google account was found with that id"))
            (fun _ -> Ok [])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.RemoveAccount "unknown"

    Assert.Equal(Some "no Google account was found with that id", AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``SetDefaultInvoiceCalendar reloads on success, showing the newly chosen calendar`` () =
    let chosen = ref None

    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" chosen.Value false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ])
            (fun accountId calendarId ->
                chosen.Value <- Some calendarId
                Ok(anAccount accountId (Some calendarId) false))

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.SetDefaultInvoiceCalendar "1" "cal-1"

    let account = AVal.force browser.AccountsAval |> List.exactlyOne
    Assert.Equal(Some "cal-1", account.DefaultInvoiceCalendarId)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed SetDefaultInvoiceCalendar surfaces the message and stops loading`` () =
    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" None false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [])
            (fun _ _ -> Error(failure "that calendar no longer exists"))

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.SetDefaultInvoiceCalendar "1" "gone"

    Assert.Equal(Some "that calendar no longer exists", AVal.force browser.ErrorAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``a later success clears an earlier error`` () =
    let attempts = ref 0

    let googleAccountApi =
        api
            (fun () -> Ok None)
            (fun _ ->
                attempts.Value <- attempts.Value + 1
                if attempts.Value = 1 then Error(failure "first attempt failed") else Ok())
            (fun () -> Ok [])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ -> Ok [])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    browser.SetClientSecret "x"
    Assert.Equal(Some "first attempt failed", AVal.force browser.ErrorAval)

    browser.SetClientSecret "x"
    Assert.Equal(None, AVal.force browser.ErrorAval)

/// Walks up from the test assembly rather than hard-coding a path, so this keeps working
/// whatever the working directory or build configuration is.
let private repositoryRoot () =
    let rec find (directory: IO.DirectoryInfo) =
        if isNull (box directory) then
            failwith "Could not locate MyDogsbody.sln above the test assembly."
        elif IO.File.Exists(IO.Path.Combine(directory.FullName, "MyDogsbody.sln")) then
            directory.FullName
        else
            find directory.Parent

    find (IO.DirectoryInfo(AppContext.BaseDirectory))

[<Fact; Trait("Level", "Unit")>]
let ``no Async.Start appears anywhere in the module creator file`` () =
    let sourceFilePath =
        IO.Path.Combine(
            repositoryRoot (),
            "MyDogsbody.UI.Portal",
            "ModuleCreators",
            "GoogleAccountsBrowserModuleCreators.fs"
        )

    Assert.True(IO.File.Exists sourceFilePath, $"Expected to find {sourceFilePath}")
    let source = IO.File.ReadAllText sourceFilePath
    Assert.DoesNotContain("Async.Start(", source)
