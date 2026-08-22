module MyDogsbody.UI.Portal.ModuleCreators.MailAccountsBrowserModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// Builds the mail accounts browser state.
///
/// `startWork` is how the module gets off the render thread. Production passes an Async.Start
/// equivalent; a test passes `fun work -> work ()` and never has to wait.
let getMailAccountsBrowserModule (startWork: (unit -> unit) -> unit) (mailAccountApi: MailAccountApi) : MailAccountsBrowserModule =
    let isLoadingCval = cval false
    let isScanningCval = cval false
    let errorCval = cval<string option> None
    let profileRootCval = cval<string option> None
    let accountsCval = cval<MailAccountUiType list> []
    let selectedAccountIdCval = cval<string option> None
    let unreadableCval = cval<UnreadableDirectoryUiType list> []
    let selectionClearedCval = cval false

    /// Reloads the accounts table and the current selection together, so the table always shows
    /// what was actually stored rather than what an action optimistically assumed.
    let loadAccounts () =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            let result = mailAccountApi.GetAccounts()

            transact (fun _ ->
                match result with
                | Ok(accounts, selected) ->
                    accountsCval.Value <- accounts
                    selectedAccountIdCval.Value <- selected
                    errorCval.Value <- None
                | Error ex -> errorCval.Value <- Some ex.Message

                isLoadingCval.Value <- false))

    let loadProfileRoot () =
        startWork (fun () ->
            let result = mailAccountApi.GetProfileRoot()

            transact (fun _ ->
                match result with
                | Ok path ->
                    profileRootCval.Value <- path
                    errorCval.Value <- None
                | Error ex -> errorCval.Value <- Some ex.Message))

    let setProfileRoot (path: string) =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            match mailAccountApi.SetProfileRoot path with
            | Ok() ->
                transact (fun _ ->
                    errorCval.Value <- None
                    isLoadingCval.Value <- false)

                // Re-read rather than echo the string handed in: the workflow validates and
                // trims, so what was stored is not always what was typed, and the page must show
                // the stored value. Same shape as scanForAccounts reloading the selection.
                loadProfileRoot ()
            | Error ex ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false))

    let scanForAccounts () =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            match mailAccountApi.ScanForAccounts() with
            | Ok discovery ->
                transact (fun _ ->
                    unreadableCval.Value <- discovery.Unreadable
                    // Set from every successful scan, not only a clearing one, so a later scan
                    // that clears nothing takes the notice back down - the same "cleared by the
                    // next success" rule ErrorAval follows.
                    selectionClearedCval.Value <- discovery.SelectionCleared
                    errorCval.Value <- None
                    isScanningCval.Value <- false)

                // A scan can reconcile the selection (an account it names may be gone), so the
                // selection is re-read from the store rather than assumed unchanged.
                loadAccounts ()
            | Error ex ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isScanningCval.Value <- false))

    /// Runs a write and reloads, so the table shows what was actually stored rather than what
    /// the action optimistically assumed.
    let write operation =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            match operation () with
            | Ok() -> loadAccounts ()
            | Error(ex: MyDogsbody.Exceptions.Types.MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false))

    loadProfileRoot ()
    loadAccounts ()

    {
        ProfileRootAval = profileRootCval
        AccountsAval = accountsCval
        SelectedAccountIdAval = selectedAccountIdCval
        UnreadableAval = unreadableCval
        SelectionClearedAval = selectionClearedCval
        IsScanningAval = isScanningCval
        IsLoadingAval = isLoadingCval
        ErrorAval = errorCval
        SetProfileRoot = setProfileRoot
        ScanForAccounts = scanForAccounts
        SelectAccount = fun id -> write (fun () -> mailAccountApi.SelectAccount id)
        CountMessages = fun id -> write (fun () -> mailAccountApi.CountMessages id |> Result.map ignore)
        ClearWatermarks = fun id -> write (fun () -> mailAccountApi.ClearWatermarks id)
    }
