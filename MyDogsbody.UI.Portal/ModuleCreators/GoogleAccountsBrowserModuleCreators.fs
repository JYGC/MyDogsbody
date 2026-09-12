module MyDogsbody.UI.Portal.ModuleCreators.GoogleAccountsBrowserModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// Builds the Google accounts browser state.
///
/// `startWork` is how the module gets off the render thread. Production passes an Async.Start
/// equivalent; a test passes `fun work -> work ()` and never has to wait.
let getGoogleAccountsBrowserModule
    (startWork: (unit -> unit) -> unit)
    (googleAccountApi: GoogleAccountApi)
    : GoogleAccountsBrowserModule =
    let isLoadingCval = cval false
    let isRegisteringCval = cval false
    let errorCval = cval<string option> None
    let clientSecretCval = cval<string option> None
    let isEditingClientSecretCval = cval false
    let accountsCval = cval<GoogleAccountUiType list> []
    let calendarsByAccountIdCval = cval<Map<string, CalendarUiType list>> Map.empty

    /// Serialises every change to the calendars map. Each change reads the map and writes back a
    /// changed copy, and production's startWork runs work on pool threads - so loadAccounts' one
    /// fetch per account finishes on several threads at once. Two of those read-then-writes
    /// interleaving drop an account's calendars without a trace: its picker empties, with neither
    /// the no-calendars caption nor an alert. The change itself still goes through transact; the
    /// lock only makes each read-then-write one step.
    let calendarsGate = obj ()

    let changeCalendars (change: Map<string, CalendarUiType list> -> Map<string, CalendarUiType list>) (alsoInTransaction: unit -> unit) =
        lock calendarsGate (fun () ->
            transact (fun _ ->
                calendarsByAccountIdCval.Value <- change calendarsByAccountIdCval.Value
                alsoInTransaction ()))

    /// Reads the stored client secret into the page, on whichever thread calls it. Never started as
    /// work of its own: its success clears the alert, so it runs ahead of the calendar fetches in
    /// the same work item - opening the page and saving a secret both do - or it can finish last and
    /// wipe the alert a failing fetch has just set.
    let reloadClientSecret () =
        let result = googleAccountApi.GetClientSecret()

        transact (fun _ ->
            match result with
            | Ok secret ->
                clientSecretCval.Value <- secret
                errorCval.Value <- None
            | Error(ex: MyDogsbodyException) -> errorCval.Value <- Some ex.Message)

    /// Loads the calendars for one account, so its picker shows that account's own list
    /// (requirements.md: "populate it from that account's own calendars"). A failure here does
    /// not disturb the accounts table - it only leaves that one picker empty - but it is surfaced
    /// via ErrorAval rather than swallowed, so "the picker is empty" comes with a reason (an
    /// expired credential, a network failure, a rate limit) instead of no explanation at all.
    ///
    /// The alert names the account by its email, the way the table does. Nothing the user did
    /// starts this fetch - the page runs one per account - so the reason on its own ("needs to be
    /// re-authorised", "Google is rate-limiting this account") leaves someone with two accounts
    /// unable to tell which one to act on.
    let loadCalendarsFor (account: GoogleAccountUiType) =
        startWork (fun () ->
            match googleAccountApi.GetCalendarsFor account.Id with
            | Ok calendars -> changeCalendars (Map.add account.Id calendars) (fun () -> errorCval.Value <- None)
            | Error(ex: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some $"Could not load the calendars for {account.EmailAddress}: {ex.Message}"))

    /// Reloads the accounts table, then the calendars for every account that has a usable
    /// credential - a NOT READY account's picker needs populating so a calendar can be chosen at
    /// all, and a READY account's needs it so the choice can be changed. Only an account flagged
    /// as needing re-authorisation is skipped, because fetching its calendars would fail anyway.
    ///
    /// So this is one `GetCalendarsFor` call per account on every reload, not a cached first
    /// fetch. Worth knowing before change #7 adds more: `CalendarRateLimited` is a real error
    /// case, and every write reloads.
    let loadAccounts () =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            let result = googleAccountApi.GetAccounts()

            transact (fun _ ->
                match result with
                | Ok accounts ->
                    accountsCval.Value <- accounts
                    errorCval.Value <- None
                | Error(ex: MyDogsbodyException) -> errorCval.Value <- Some ex.Message

                isLoadingCval.Value <- false)

            match result with
            | Ok accounts ->
                for account in accounts do
                    if not account.NeedsReauthorisation then
                        loadCalendarsFor account
            | Error _ -> ())

    let startEditingClientSecret () = transact (fun _ -> isEditingClientSecretCval.Value <- true)

    let cancelEditingClientSecret () = transact (fun _ -> isEditingClientSecretCval.Value <- false)

    let setClientSecret (secret: string) =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            match googleAccountApi.SetClientSecret secret with
            | Ok() ->
                transact (fun _ ->
                    errorCval.Value <- None
                    isEditingClientSecretCval.Value <- false
                    isLoadingCval.Value <- false)

                // Every calendar fetch parses the stored secret first, so the calendars are fetched
                // again: a corrected secret fills the pickers a malformed one left empty, and a bad
                // paste is reported now rather than at the next page load. The secret is re-read
                // first, here rather than as work of its own - its success clears the alert, so it
                // has to finish before a failing fetch can set one.
                reloadClientSecret ()
                loadAccounts ()
            | Error ex ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false))

    let registerAccount () =
        transact (fun _ -> isRegisteringCval.Value <- true)

        startWork (fun () ->
            match googleAccountApi.RegisterAccount() with
            | Ok _ ->
                transact (fun _ ->
                    errorCval.Value <- None
                    isRegisteringCval.Value <- false)

                loadAccounts ()
            | Error ex ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isRegisteringCval.Value <- false))

    let reauthoriseAccount (accountId: string) =
        transact (fun _ -> isRegisteringCval.Value <- true)

        startWork (fun () ->
            match googleAccountApi.ReauthoriseAccount accountId with
            | Ok _ ->
                transact (fun _ ->
                    errorCval.Value <- None
                    isRegisteringCval.Value <- false)

                loadAccounts ()
            | Error ex ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isRegisteringCval.Value <- false))

    let removeAccount (accountId: string) =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            match googleAccountApi.RemoveAccount accountId with
            | Ok() ->
                changeCalendars (Map.remove accountId) (fun () -> errorCval.Value <- None)

                loadAccounts ()
            | Error ex ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false))

    /// Runs a write that produces an updated account and reloads, so the table shows what was
    /// actually stored rather than what the action optimistically assumed - the convention every
    /// module creator in this codebase follows.
    let setDefaultInvoiceCalendar (accountId: string) (calendarId: string) =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            match googleAccountApi.SetDefaultInvoiceCalendar accountId calendarId with
            | Ok _ ->
                transact (fun _ -> errorCval.Value <- None)
                loadAccounts ()
            | Error ex ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false))

    // Opening the page: the secret, then the accounts and their calendars, in one work item for the
    // reason `reloadClientSecret` gives. The spinner is set here, before any work runs, so the first
    // render shows the table loading rather than "No Google accounts registered yet."
    transact (fun _ -> isLoadingCval.Value <- true)

    startWork (fun () ->
        reloadClientSecret ()
        loadAccounts ())

    {
        ClientSecretAval = clientSecretCval
        IsEditingClientSecretAval = isEditingClientSecretCval
        AccountsAval = accountsCval
        CalendarsByAccountIdAval = calendarsByAccountIdCval
        IsLoadingAval = isLoadingCval
        IsRegisteringAval = isRegisteringCval
        ErrorAval = errorCval
        StartEditingClientSecret = startEditingClientSecret
        CancelEditingClientSecret = cancelEditingClientSecret
        SetClientSecret = setClientSecret
        RegisterAccount = registerAccount
        ReauthoriseAccount = reauthoriseAccount
        RemoveAccount = removeAccount
        SetDefaultInvoiceCalendar = setDefaultInvoiceCalendar
    }
