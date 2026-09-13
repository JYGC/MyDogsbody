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
            (fun _ -> Error(failure "This Google account needs to be re-authorised."))
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    Assert.Equal(
        Some "Could not load the calendars for 1@example.com: This Google account needs to be re-authorised.",
        AVal.force browser.ErrorAval
    )
    Assert.Empty(AVal.force browser.CalendarsByAccountIdAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed calendar fetch names the account it failed for`` () =
    // The page fetches every account's calendars by itself, so nothing the user did says which
    // account a failure belongs to - and the page names accounts only by email. The reason on its
    // own ("needs to be re-authorised", "Google is rate-limiting this account") left a user with
    // two accounts unable to tell which one to act on.
    let googleAccountApi =
        api
            (fun () -> Ok(Some "secret"))
            (fun _ -> Ok())
            (fun () -> Ok [ anAccount "1" None false; anAccount "2" None false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun id ->
                if id = "2" then
                    Error(failure "This Google account needs to be re-authorised.")
                else
                    Ok [ aCalendar "cal-1" "Invoices" true ])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    Assert.Equal(
        Some "Could not load the calendars for 2@example.com: This Google account needs to be re-authorised.",
        AVal.force browser.ErrorAval
    )

    let byAccount = AVal.force browser.CalendarsByAccountIdAval
    Assert.Equal<CalendarUiType list option>(Some [ aCalendar "cal-1" "Invoices" true ], Map.tryFind "1" byAccount)
    Assert.Equal<CalendarUiType list option>(None, Map.tryFind "2" byAccount)
    Assert.False(AVal.force browser.IsLoadingAval)

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

[<Fact; Trait("Level", "Unit")>]
let ``calendars fetched for several accounts at once all reach the picker map`` () =
    // Production's startWork runs each piece of work on its own pool thread, so loadAccounts' one
    // fetch per account finishes on several threads at once, and each adds its account's calendars
    // to the one shared map. Two finishing together must not lose either: a lost entry is an empty
    // picker with neither the no-calendars caption nor an alert, because for that account nothing
    // failed and nothing came back empty - its calendars are simply not there.
    let accountCount = 8
    let ids = [ for i in 1..accountCount -> string i ]
    let started = Collections.Concurrent.ConcurrentQueue<Threading.Thread>()

    let onItsOwnThread (work: unit -> unit) =
        let thread = Threading.Thread(work)
        started.Enqueue thread
        thread.Start()

    // Work started by work (loadAccounts starting each fetch) is queued before its parent ends,
    // so joining in queue order reaches every thread.
    let rec waitForAllWork () =
        match started.TryDequeue() with
        | true, thread ->
            Assert.True(thread.Join(TimeSpan.FromSeconds 10.0), "Work did not finish within ten seconds.")
            waitForAllWork ()
        | _ -> ()

    for trial in 1..20 do
        use together = new Threading.Barrier(accountCount)

        let googleAccountApi =
            api
                (fun () -> Ok(Some "secret"))
                (fun _ -> Ok())
                (fun () -> Ok [ for id in ids -> anAccount id None false ])
                (fun () -> failwith "unused")
                (fun _ -> failwith "unused")
                (fun _ -> Ok())
                (fun id ->
                    // Every fetch returns at the same instant - the moment the threads race on.
                    together.SignalAndWait(TimeSpan.FromSeconds 10.0) |> ignore
                    Ok [ aCalendar $"cal-{id}" $"Calendar {id}" true ])
                (fun _ _ -> failwith "unused")

        let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule onItsOwnThread googleAccountApi
        waitForAllWork ()

        let byAccount = AVal.force browser.CalendarsByAccountIdAval
        let missing = ids |> List.filter (fun id -> not (Map.containsKey id byAccount))
        Assert.True(List.isEmpty missing, $"Trial {trial}: calendars lost for accounts %A{missing}.")

        for id in ids do
            Assert.Equal<CalendarUiType list>([ aCalendar $"cal-{id}" $"Calendar {id}" true ], Map.find id byAccount)

/// Holds work back and runs it newest first when the test says so - a pool thread can finish work in
/// the opposite order it was started, and this is that order made deterministic.
let private newestFirst () =
    let pending = Collections.Generic.Stack<unit -> unit>()
    let startWork (work: unit -> unit) = pending.Push work

    let rec runAll () =
        if pending.Count > 0 then
            (pending.Pop()) ()
            runAll ()

    startWork, runAll

let private malformedSecret = "The stored Google client secret is malformed."

[<Fact; Trait("Level", "Unit")>]
let ``correcting a malformed client secret loads the calendars it could not`` () =
    // Every calendar fetch parses the stored client secret first, so a malformed one leaves every
    // picker empty beside the alert. Fixing the secret clears that alert - and unless the calendars
    // are fetched again, leaves each picker empty with neither the alert nor the no-calendars caption.
    let stored = ref (Some "not-json")

    let googleAccountApi =
        api
            (fun () -> Ok stored.Value)
            (fun secret ->
                stored.Value <- Some secret
                Ok())
            (fun () -> Ok [ anAccount "1" None false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ ->
                if stored.Value = Some "not-json" then
                    Error(failure malformedSecret)
                else
                    Ok [ aCalendar "cal-1" "Invoices" true ])
            (fun _ _ -> failwith "unused")

    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule runSynchronously googleAccountApi

    Assert.Equal(Some $"Could not load the calendars for 1@example.com: {malformedSecret}", AVal.force browser.ErrorAval)
    Assert.Equal<CalendarUiType list option>(None, Map.tryFind "1" (AVal.force browser.CalendarsByAccountIdAval))

    browser.StartEditingClientSecret()
    browser.SetClientSecret "the-corrected-secret"

    Assert.Equal<CalendarUiType list option>(
        Some [ aCalendar "cal-1" "Invoices" true ],
        Map.tryFind "1" (AVal.force browser.CalendarsByAccountIdAval)
    )

    Assert.Equal(None, AVal.force browser.ErrorAval)
    Assert.Equal(Some "the-corrected-secret", AVal.force browser.ClientSecretAval)
    Assert.False(AVal.force browser.IsEditingClientSecretAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``replacing the client secret with one that does not work says so straight away`` () =
    // SetClientSecret stores whatever it is given. A bad paste over a working secret breaks every
    // calendar fetch, and the page has to say so when it happens rather than at the next page load.
    // Work finishes newest first here: re-reading the secret succeeds and clears the alert, so it has
    // to be done before the calendars are fetched, never alongside them.
    let stored = ref (Some "the-working-secret")
    let fetches = ref 0

    let googleAccountApi =
        api
            (fun () -> Ok stored.Value)
            (fun secret ->
                stored.Value <- Some secret
                Ok())
            (fun () -> Ok [ anAccount "1" None false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> Ok())
            (fun _ ->
                fetches.Value <- fetches.Value + 1

                if stored.Value = Some "the-working-secret" then
                    Ok [ aCalendar "cal-1" "Invoices" true ]
                else
                    Error(failure malformedSecret))
            (fun _ _ -> failwith "unused")

    let startWork, runAll = newestFirst ()
    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule startWork googleAccountApi
    runAll ()

    Assert.Equal(1, fetches.Value)
    Assert.Equal(None, AVal.force browser.ErrorAval)

    browser.StartEditingClientSecret()
    browser.SetClientSecret "not-json"
    runAll ()

    Assert.Equal(2, fetches.Value)
    Assert.Equal(Some $"Could not load the calendars for 1@example.com: {malformedSecret}", AVal.force browser.ErrorAval)
    Assert.Equal(Some "not-json", AVal.force browser.ClientSecretAval)
    // The last list that did load stays beside the alert until a fetch replaces it.
    Assert.Equal<CalendarUiType list option>(
        Some [ aCalendar "cal-1" "Invoices" true ],
        Map.tryFind "1" (AVal.force browser.CalendarsByAccountIdAval)
    )

    Assert.False(AVal.force browser.IsEditingClientSecretAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``opening the page over a malformed client secret keeps the alert its calendar fetch set`` () =
    // Reading the stored secret succeeds and clears the alert, while every calendar fetch fails on
    // that same secret. Work finishes newest first here, as a pool thread can finish it: if the page
    // starts the read as work of its own beside the accounts load, the read can finish last and wipe
    // the fetch's alert - leaving every picker empty with neither the alert nor the no-calendars
    // caption, which is what saving the secret already guards against.
    let fetches = ref 0

    let googleAccountApi =
        api
            (fun () -> Ok(Some "not-json"))
            (fun _ -> failwith "unused")
            (fun () -> Ok [ anAccount "1" None false ])
            (fun () -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ -> failwith "unused")
            (fun _ ->
                fetches.Value <- fetches.Value + 1
                Error(failure malformedSecret))
            (fun _ _ -> failwith "unused")

    let startWork, runAll = newestFirst ()
    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule startWork googleAccountApi
    runAll ()

    Assert.Equal(1, fetches.Value)
    Assert.Equal(Some $"Could not load the calendars for 1@example.com: {malformedSecret}", AVal.force browser.ErrorAval)
    Assert.Equal(Some "not-json", AVal.force browser.ClientSecretAval)
    Assert.Equal<GoogleAccountUiType list>([ anAccount "1" None false ], AVal.force browser.AccountsAval)
    Assert.Equal<CalendarUiType list option>(None, Map.tryFind "1" (AVal.force browser.CalendarsByAccountIdAval))
    Assert.False(AVal.force browser.IsLoadingAval)
    Assert.False(AVal.force browser.IsEditingClientSecretAval)

[<Fact; Trait("Level", "Unit")>]
let ``opening the page shows the accounts table loading before any work has run`` () =
    // The table's spinner is set where the page is built, not when its work first runs - so the
    // first render never claims "No Google accounts registered yet." for a store not read yet.
    let startWork, _ = newestFirst ()
    let browser = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule startWork (defaultApi ())

    Assert.True(AVal.force browser.IsLoadingAval)
