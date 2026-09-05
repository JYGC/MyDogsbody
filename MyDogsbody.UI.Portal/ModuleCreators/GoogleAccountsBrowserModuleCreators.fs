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

    let loadClientSecret () =
        startWork (fun () ->
            let result = googleAccountApi.GetClientSecret()

            transact (fun _ ->
                match result with
                | Ok secret ->
                    clientSecretCval.Value <- secret
                    errorCval.Value <- None
                | Error(ex: MyDogsbodyException) -> errorCval.Value <- Some ex.Message))

    /// Loads the calendars for one account, so its picker shows that account's own list
    /// (requirements.md: "populate it from that account's own calendars"). A failure here does
    /// not disturb the accounts table - it only leaves that one picker empty - but it is surfaced
    /// via ErrorAval rather than swallowed, so "the picker is empty" comes with a reason (an
    /// expired credential, a network failure, a rate limit) instead of no explanation at all.
    let loadCalendarsFor (accountId: string) =
        startWork (fun () ->
            match googleAccountApi.GetCalendarsFor accountId with
            | Ok calendars ->
                transact (fun _ ->
                    calendarsByAccountIdCval.Value <- calendarsByAccountIdCval.Value |> Map.add accountId calendars
                    errorCval.Value <- None)
            | Error(ex: MyDogsbodyException) -> transact (fun _ -> errorCval.Value <- Some ex.Message))

    /// Reloads the accounts table, then the calendars for every account with no default chosen
    /// (a NOT READY account's picker needs to be populated to let the user choose one) or whose
    /// stored default might no longer exist. Ready accounts are not re-fetched on every reload -
    /// their picker already has what it needs once loaded.
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
                        loadCalendarsFor account.Id
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

                loadClientSecret ()
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
                transact (fun _ ->
                    errorCval.Value <- None
                    calendarsByAccountIdCval.Value <- calendarsByAccountIdCval.Value |> Map.remove accountId)

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

    loadClientSecret ()
    loadAccounts ()

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
