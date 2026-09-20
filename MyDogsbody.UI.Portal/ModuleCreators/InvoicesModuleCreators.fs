module MyDogsbody.UI.Portal.ModuleCreators.InvoicesModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// Each read used to be matched on its own and all but one discarded its error with `| Error _ ->
/// ()`, so a read that failed while the operation the user actually asked for succeeded set no
/// alert at all. The ledger read is the one that costs: the initial page load scans, so a ledger
/// read that failed left an EMPTY table under a "0 invoice(s)" count line with nothing on screen
/// to say that anything had gone wrong - the stored invoices were there, unreadable, and the page
/// said there were none.
///
/// Order is priority, and the operation the user pressed comes first: a failed mailbox scan is
/// the news, and the stored ledger read alongside it is still on screen.
let private messageOfTheFirstFailedReadInTheOrderGivenOrNoneWhenEveryOneSucceeded
    (results: Result<unit, MyDogsbodyException> list)
    : string option =
    results
    |> List.tryPick (function
        | Error(caughtException: MyDogsbodyException) -> Some caughtException.Message
        | Ok() -> None)

let private syncViewHeldBeforeTheFirstLoadSyncPlanCompletesNotTheSameAsUpToDate: SyncViewUiType =
    { StatusByInvoiceId = Map.empty
      Plan = []
      OrphanedEvents = []
      NotReadyReason = None }

/// The plan rows a "sync now" press would act on right now: everything outstanding when nothing is
/// ticked (Q2.7 - and passed to ExecuteSyncPlan as `[]`, letting the API re-derive it rather than
/// trusting a possibly-stale preview), or the ticked subset when something is. A delete row carries
/// no InvoiceId (its invoice has already left the ledger), so it can never be individually ticked by
/// invoice id - the simplest compliant design named in this change's brief: a delete only ever rides
/// along in "everything outstanding" mode.
let private rowsMatchingSelection (selectedInvoiceIds: Set<string>) (syncView: SyncViewUiType) : SyncPlanRowUiType list =
    if Set.isEmpty selectedInvoiceIds then
        syncView.Plan
    else
        syncView.Plan
        |> List.filter (fun planRow ->
            match planRow.InvoiceId with
            | Some invoiceId -> Set.contains invoiceId selectedInvoiceIds
            | None -> false)

/// Task 8.3: an invoice that cannot become a calendar event carries no sync status regardless of
/// what the map says - it won't have an entry anyway (`UploadableInvoice.ofStored` already
/// excludes it), but staying explicit here means that stays true even if the map's population ever
/// changes.
let private overlaySyncStatus (syncView: SyncViewUiType) (invoice: InvoiceUiType) : InvoiceUiType =
    if invoice.CanBecomeCalendarEvent then
        { invoice with SyncStatus = Map.tryFind invoice.Id syncView.StatusByInvoiceId }
    else
        { invoice with SyncStatus = None }

/// startWork is how the module gets off the render thread; a test passes `fun work -> work ()`.
/// This file never starts a background thread of its own - that is startWork's job.
let getInvoicesModule
    (startWork: (unit -> unit) -> unit)
    (invoiceApi: InvoiceApi)
    (scanWindowApi: ScanWindowApi)
    (invoiceSyncApi: InvoiceSyncApi)
    : InvoicesModule =
    // Holds whatever InvoiceApi.GetInvoices returned, with no sync status - the public InvoicesAval
    // below overlays syncViewCval onto this every time either changes, so the two never drift out of
    // step by hand.
    let rawInvoicesCval = cval<InvoiceUiType list> []
    let problemsCval = cval<ScanProblemUiType list> []
    let tombstonesCval = cval<TombstoneUiType list> []
    let windowsCval = cval<ScanWindowUiType list> []
    // 0 until resolved from the API - never a literal 14 in this file.
    let selectedDaysCval = cval 0
    let isScanningCval = cval false
    let errorCval = cval<string option> None

    // --- change #7: calendar sync ---
    let syncViewCval = cval<SyncViewUiType> syncViewHeldBeforeTheFirstLoadSyncPlanCompletesNotTheSameAsUpToDate
    let isSyncingCval = cval false
    // View state only (task 8.2) - never read from or written to any API, so a rescan can just
    // reset it without undoing anything persisted.
    let selectedInvoiceIdsCval = cval<Set<string>> Set.empty
    let lastSyncOutcomesCval = cval<SyncOutcomeRowUiType list> []

    let invoicesAval = AVal.map2 (fun (invoices: InvoiceUiType list) syncView -> invoices |> List.map (overlaySyncStatus syncView)) rawInvoicesCval syncViewCval

    let pendingActionCountAval =
        AVal.map2 (fun selectedInvoiceIds syncView -> List.length (rowsMatchingSelection selectedInvoiceIds syncView)) selectedInvoiceIdsCval syncViewCval

    let setError (result: Result<_, MyDogsbodyException>) =
        match result with
        | Ok _ -> errorCval.Value <- None
        | Error(caughtException: MyDogsbodyException) -> errorCval.Value <- Some caughtException.Message

    /// Show the stored ledger for the given window - `GetInvoices` / `GetProblems`, no mailbox
    /// read. This is what a window change does: task 12.4 measured a full scan at ~60 s whatever
    /// the window (the cost is reading every folder, not the cutoff), so Q1.9's immediate-rescan
    /// is dropped for the explicit `rescan` below. "Narrowing hides, it does not forget" - the
    /// store keeps every invoice; the window only decides which are shown.
    let loadLedger (days: int) =
        transact (fun _ ->
            isScanningCval.Value <- true
            // A window change re-queries the ledger, so a tick against a row that may no longer be
            // in the result is worse than no tick at all (task 8.2).
            selectedInvoiceIdsCval.Value <- Set.empty)

        startWork (fun () ->
            let windows = scanWindowApi.GetScanWindows()
            let ledger = invoiceApi.GetInvoices days
            let problems = invoiceApi.GetProblems()
            let syncPlan = invoiceSyncApi.GetSyncPlan()

            transact (fun _ ->
                match windows with
                | Ok scanWindows -> windowsCval.Value <- scanWindows
                | Error _ -> ()

                match ledger with
                | Ok invoices -> rawInvoicesCval.Value <- invoices
                | Error _ -> ()

                match problems with
                | Ok scanProblems -> problemsCval.Value <- scanProblems
                | Error _ -> ()

                match syncPlan with
                | Ok syncView -> syncViewCval.Value <- syncView
                | Error _ -> ()

                errorCval.Value <-
                    messageOfTheFirstFailedReadInTheOrderGivenOrNoneWhenEveryOneSucceeded
                        [ ledger |> Result.map ignore
                          problems |> Result.map ignore
                          windows |> Result.map ignore
                          syncPlan |> Result.map ignore ]

                selectedDaysCval.Value <- days
                isScanningCval.Value <- false))

    /// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.UI.Portal.md - InvoicesModuleCreators.fs: scanUsing
    let scanUsing (scanOperation: int -> Result<ScanResultUiType, MyDogsbodyException>) (days: int) =
        transact (fun _ ->
            isScanningCval.Value <- true
            // Same reasoning as loadLedger: freshly-scanned mail can change which rows exist at all.
            selectedInvoiceIdsCval.Value <- Set.empty)

        startWork (fun () ->
            let windows = scanWindowApi.GetScanWindows()
            let scanResult = scanOperation days
            let ledger = invoiceApi.GetInvoices days
            let problems = invoiceApi.GetProblems()
            let syncPlan = invoiceSyncApi.GetSyncPlan()

            transact (fun _ ->
                match windows with
                | Ok scanWindows -> windowsCval.Value <- scanWindows
                | Error _ -> ()

                match ledger with
                | Ok invoices -> rawInvoicesCval.Value <- invoices
                | Error _ -> ()

                match problems with
                | Ok scanProblems -> problemsCval.Value <- scanProblems
                | Error _ -> ()

                match syncPlan with
                | Ok syncView -> syncViewCval.Value <- syncView
                | Error _ -> ()

                // The scan comes first: when the mailbox read failed that is the news, and the
                // stored ledger read alongside it is still on screen. When it SUCCEEDED, a failed
                // ledger, problems or sync-plan read is the only thing that can report itself.
                errorCval.Value <-
                    messageOfTheFirstFailedReadInTheOrderGivenOrNoneWhenEveryOneSucceeded
                        [ scanResult |> Result.map ignore
                          ledger |> Result.map ignore
                          problems |> Result.map ignore
                          windows |> Result.map ignore
                          syncPlan |> Result.map ignore ]

                selectedDaysCval.Value <- days
                isScanningCval.Value <- false))

    /// "Scan now": read the mailbox, resuming each folder from its watermark.
    let scan = scanUsing invoiceApi.Scan

    /// "Rescan everything": discard the selected account's watermarks, then read every folder in
    /// full. For mail a plain `scan` resumes straight past because its folder was read before the
    /// supplier or template existed.
    let rescanEverything = scanUsing invoiceApi.RescanEverything

    /// The initial load: resolve the remembered window through the API, then scan it once so the
    /// ledger is populated. Only a *window change* after this stops scanning.
    let start () =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            match scanWindowApi.GetSelectedScanWindow() with
            | Ok days -> scan days
            | Error(caughtException: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some caughtException.Message
                    isScanningCval.Value <- false))

    /// Persist the choice, then reload the stored ledger for it - NOT a scan (see `loadLedger`).
    let selectWindow (days: int) =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            match scanWindowApi.SelectScanWindow days with
            | Ok() -> loadLedger days
            | Error(caughtException: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some caughtException.Message
                    isScanningCval.Value <- false))

    let deleteInvoice (id: string) =
        startWork (fun () ->
            let result = invoiceApi.DeleteInvoice id
            transact (fun _ -> setError result)

            match result with
            // the row is hard-deleted, so a reload is enough - no need to re-read the mailbox. Drop
            // it from the selection too: a tick against a row that no longer exists is worse than
            // no tick at all (task 8.2's rationale, applied here as well as to a rescan).
            | Ok() ->
                transact (fun _ -> selectedInvoiceIdsCval.Value <- Set.remove id selectedInvoiceIdsCval.Value)
                loadLedger selectedDaysCval.Value
            | Error _ -> ())

    let undeleteInvoice (supplierId: string) (reference: string) =
        startWork (fun () ->
            let result = invoiceApi.UndeleteInvoice supplierId reference
            transact (fun _ -> setError result)

            match result with
            // un-delete only removes the tombstone; the invoice row is gone, so only a scan of a
            // covering window can put it back (UndeleteInvoiceWorkflow says as much).
            | Ok() -> scan selectedDaysCval.Value
            | Error _ -> ())

    let loadProblems () =
        startWork (fun () ->
            let result = invoiceApi.GetProblems()

            transact (fun _ ->
                match result with
                | Ok problems ->
                    problemsCval.Value <- problems
                    errorCval.Value <- None
                | Error(caughtException: MyDogsbodyException) -> errorCval.Value <- Some caughtException.Message))

    let loadTombstones () =
        startWork (fun () ->
            let result = invoiceApi.GetTombstones()

            transact (fun _ ->
                match result with
                | Ok tombstones ->
                    tombstonesCval.Value <- tombstones
                    errorCval.Value <- None
                | Error(caughtException: MyDogsbodyException) -> errorCval.Value <- Some caughtException.Message))

    // --- change #7: calendar sync ---

    /// A standalone reload of the sync plan, in the same shape as `loadProblems` / `loadTombstones`:
    /// its own alert, set or cleared on its own result. `loadLedger` and `scanUsing` also fold a
    /// sync-plan read into their own composite picture (lowest priority in the list of reads they
    /// report the first failure of), so this exists for a caller that wants the plan refreshed on
    /// its own - after `executeSync`, in particular.
    let loadSyncPlan () =
        transact (fun _ -> isSyncingCval.Value <- true)

        startWork (fun () ->
            let result = invoiceSyncApi.GetSyncPlan()

            transact (fun _ ->
                match result with
                | Ok syncView ->
                    syncViewCval.Value <- syncView
                    errorCval.Value <- None
                | Error(caughtException: MyDogsbodyException) -> errorCval.Value <- Some caughtException.Message

                isSyncingCval.Value <- false))

    let toggleInvoice (invoiceId: string) =
        transact (fun _ ->
            let currentSelection = selectedInvoiceIdsCval.Value

            selectedInvoiceIdsCval.Value <-
                if Set.contains invoiceId currentSelection then
                    Set.remove invoiceId currentSelection
                else
                    Set.add invoiceId currentSelection)

    let clearSelection () =
        transact (fun _ -> selectedInvoiceIdsCval.Value <- Set.empty)

    /// Runs ExecuteSyncPlan over the current selection, or `[]` for "everything outstanding" when
    /// nothing is ticked (Q2.7) - `[]` rather than the locally-held plan, so the API re-derives a
    /// fresh plan instead of trusting a preview the ledger or calendar may have since moved past.
    /// A write reloads (CLAUDE-project.md: "every command that changes stored data calls the load
    /// function on success") - ON SUCCESS ONLY. Reloading unconditionally would run `loadSyncPlan`
    /// even after a failed execute, and its own success overwrites the alert this function just set
    /// with None before the user ever sees it.
    let executeSync () =
        transact (fun _ -> isSyncingCval.Value <- true)

        startWork (fun () ->
            let selectedInvoiceIds = AVal.force selectedInvoiceIdsCval

            let rowsToRun =
                if Set.isEmpty selectedInvoiceIds then
                    []
                else
                    rowsMatchingSelection selectedInvoiceIds (AVal.force syncViewCval)

            let result = invoiceSyncApi.ExecuteSyncPlan rowsToRun

            match result with
            | Ok outcomes ->
                transact (fun _ ->
                    lastSyncOutcomesCval.Value <- outcomes
                    errorCval.Value <- None
                    // only clear on success - a failed run leaves the ticks so the user can retry
                    selectedInvoiceIdsCval.Value <- Set.empty
                    isSyncingCval.Value <- false)

                loadSyncPlan ()
            | Error(caughtException: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some caughtException.Message
                    isSyncingCval.Value <- false))

    start ()

    { InvoicesAval = invoicesAval
      ProblemsAval = problemsCval
      TombstonesAval = tombstonesCval
      ScanWindowsAval = windowsCval
      SelectedWindowDaysAval = selectedDaysCval
      IsScanningAval = isScanningCval
      ErrorAval = errorCval
      SelectWindow = selectWindow
      Rescan = fun () -> scan selectedDaysCval.Value
      RescanEverything = fun () -> rescanEverything selectedDaysCval.Value
      DeleteInvoice = deleteInvoice
      UndeleteInvoice = undeleteInvoice
      LoadProblems = loadProblems
      LoadTombstones = loadTombstones
      SyncViewAval = syncViewCval
      IsSyncingAval = isSyncingCval
      LoadSyncPlan = loadSyncPlan
      SelectedInvoiceIdsAval = selectedInvoiceIdsCval
      ToggleInvoice = toggleInvoice
      ClearSelection = clearSelection
      PendingActionCountAval = pendingActionCountAval
      ExecuteSync = executeSync
      LastSyncOutcomesAval = lastSyncOutcomesCval }

/// Builds the /settings/scan-windows page state.
let getScanWindowsBrowserModule
    (startWork: (unit -> unit) -> unit)
    (scanWindowApi: ScanWindowApi)
    : ScanWindowsBrowserModule =
    let windowsCval = cval<ScanWindowUiType list> []
    let selectedDaysCval = cval 0
    let isLoadingCval = cval false
    let errorCval = cval<string option> None

    let load () =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            let windows = scanWindowApi.GetScanWindows()
            let selected = scanWindowApi.GetSelectedScanWindow()

            transact (fun _ ->
                match windows with
                | Ok scanWindows -> windowsCval.Value <- scanWindows
                | Error _ -> ()

                match selected with
                | Ok days -> selectedDaysCval.Value <- days
                | Error _ -> ()

                // A failed selected-window read used to be discarded, which left the page marking
                // nothing as "(current)" and saying nothing about why.
                errorCval.Value <-
                    messageOfTheFirstFailedReadInTheOrderGivenOrNoneWhenEveryOneSucceeded
                        [ windows |> Result.map ignore; selected |> Result.map ignore ]

                isLoadingCval.Value <- false))

    let write operation =
        transact (fun _ -> isLoadingCval.Value <- true)

        startWork (fun () ->
            match operation () with
            | Ok() ->
                transact (fun _ ->
                    errorCval.Value <- None
                    isLoadingCval.Value <- false)

                load ()
            | Error(caughtException: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some caughtException.Message
                    isLoadingCval.Value <- false))

    load ()

    { WindowsAval = windowsCval
      SelectedWindowDaysAval = selectedDaysCval
      IsLoadingAval = isLoadingCval
      ErrorAval = errorCval
      Load = load
      AddWindow = fun days -> write (fun () -> scanWindowApi.AddScanWindow days)
      DeleteWindow = fun id -> write (fun () -> scanWindowApi.DeleteScanWindow id) }
