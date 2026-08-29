module MyDogsbody.UI.Portal.ModuleCreators.InvoicesModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// The alert for a load that performs several reads: the message of the FIRST one that failed,
/// in the order given, or None when every one of them succeeded.
///
/// Each read used to be matched on its own and all but one discarded its error with `| Error _ ->
/// ()`, so a read that failed while the operation the user actually asked for succeeded set no
/// alert at all. The ledger read is the one that costs: the initial page load scans, so a ledger
/// read that failed left an EMPTY table under a "0 invoice(s)" count line with nothing on screen
/// to say that anything had gone wrong - the stored invoices were there, unreadable, and the page
/// said there were none.
///
/// Order is priority, and the operation the user pressed comes first: a failed mailbox scan is
/// the news, and the stored ledger read alongside it is still on screen.
let private firstFailure (results: Result<unit, MyDogsbodyException> list) : string option =
    results
    |> List.tryPick (function
        | Error(ex: MyDogsbodyException) -> Some ex.Message
        | Ok() -> None)

/// Builds the invoices page state.
///
/// startWork is how the module gets off the render thread; a test passes `fun work -> work ()`.
/// This file never starts a background thread of its own - that is startWork's job.
let getInvoicesModule
    (startWork: (unit -> unit) -> unit)
    (invoiceApi: InvoiceApi)
    (scanWindowApi: ScanWindowApi)
    : InvoicesModule =
    let invoicesCval = cval<InvoiceUiType list> []
    let problemsCval = cval<ScanProblemUiType list> []
    let tombstonesCval = cval<TombstoneUiType list> []
    let windowsCval = cval<ScanWindowUiType list> []
    // 0 until resolved from the API - never a literal 14 in this file.
    let selectedDaysCval = cval 0
    let isScanningCval = cval false
    let errorCval = cval<string option> None

    let setError (result: Result<_, MyDogsbodyException>) =
        match result with
        | Ok _ -> errorCval.Value <- None
        | Error(ex: MyDogsbodyException) -> errorCval.Value <- Some ex.Message

    /// Show the stored ledger for the given window - `GetInvoices` / `GetProblems`, no mailbox
    /// read. This is what a window change does: task 12.4 measured a full scan at ~60 s whatever
    /// the window (the cost is reading every folder, not the cutoff), so Q1.9's immediate-rescan
    /// is dropped for the explicit `rescan` below. "Narrowing hides, it does not forget" - the
    /// store keeps every invoice; the window only decides which are shown.
    let loadLedger (days: int) =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            let windows = scanWindowApi.GetScanWindows()
            let ledger = invoiceApi.GetInvoices days
            let problems = invoiceApi.GetProblems()

            transact (fun _ ->
                match windows with
                | Ok ws -> windowsCval.Value <- ws
                | Error _ -> ()

                match ledger with
                | Ok invoices -> invoicesCval.Value <- invoices
                | Error _ -> ()

                match problems with
                | Ok ps -> problemsCval.Value <- ps
                | Error _ -> ()

                errorCval.Value <-
                    firstFailure
                        [ ledger |> Result.map ignore
                          problems |> Result.map ignore
                          windows |> Result.map ignore ]

                selectedDaysCval.Value <- days
                isScanningCval.Value <- false))

    /// Read the mailbox for `days`, then show the stored ledger for it. The scan may fail (no
    /// mail account, an unreachable store) - the stored ledger stays on screen with the alert.
    ///
    /// The page's view comes from `GetInvoices` / `GetProblems`, never from `ScanResult`: that
    /// carries only what THIS scan did (design decision 6), and watermarks mean a scan of an
    /// unchanged mailbox reads no messages and so returns two empty lists. Taking the table from
    /// it blanked a ledger that was still stored - on the initial load too, since `start` scans,
    /// so a returning user opened on an empty table. Q1.19 says the same of problems: they are
    /// persisted precisely "so incremental scanning does not empty the diagnostic list before it
    /// is looked at". Read AFTER the scan, so whatever it just stored is included.
    let scan (days: int) =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            let windows = scanWindowApi.GetScanWindows()
            let scanResult = invoiceApi.Scan days
            let ledger = invoiceApi.GetInvoices days
            let problems = invoiceApi.GetProblems()

            transact (fun _ ->
                match windows with
                | Ok ws -> windowsCval.Value <- ws
                | Error _ -> ()

                match ledger with
                | Ok invoices -> invoicesCval.Value <- invoices
                | Error _ -> ()

                match problems with
                | Ok ps -> problemsCval.Value <- ps
                | Error _ -> ()

                // The scan comes first: when the mailbox read failed that is the news, and the
                // stored ledger read alongside it is still on screen. When it SUCCEEDED, a failed
                // ledger or problems read is the only thing that can report itself.
                errorCval.Value <-
                    firstFailure
                        [ scanResult |> Result.map ignore
                          ledger |> Result.map ignore
                          problems |> Result.map ignore
                          windows |> Result.map ignore ]

                selectedDaysCval.Value <- days
                isScanningCval.Value <- false))

    /// The initial load: resolve the remembered window through the API, then scan it once so the
    /// ledger is populated. Only a *window change* after this stops scanning.
    let start () =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            match scanWindowApi.GetSelectedScanWindow() with
            | Ok days -> scan days
            | Error(ex: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isScanningCval.Value <- false))

    /// Persist the choice, then reload the stored ledger for it - NOT a scan (see `loadLedger`).
    let selectWindow (days: int) =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            match scanWindowApi.SelectScanWindow days with
            | Ok() -> loadLedger days
            | Error(ex: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isScanningCval.Value <- false))

    let deleteInvoice (id: string) =
        startWork (fun () ->
            let result = invoiceApi.DeleteInvoice id
            transact (fun _ -> setError result)

            match result with
            // the row is hard-deleted, so a reload is enough - no need to re-read the mailbox
            | Ok() -> loadLedger selectedDaysCval.Value
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
                | Error(ex: MyDogsbodyException) -> errorCval.Value <- Some ex.Message))

    let loadTombstones () =
        startWork (fun () ->
            let result = invoiceApi.GetTombstones()

            transact (fun _ ->
                match result with
                | Ok tombstones ->
                    tombstonesCval.Value <- tombstones
                    errorCval.Value <- None
                | Error(ex: MyDogsbodyException) -> errorCval.Value <- Some ex.Message))

    start ()

    { InvoicesAval = invoicesCval
      ProblemsAval = problemsCval
      TombstonesAval = tombstonesCval
      ScanWindowsAval = windowsCval
      SelectedWindowDaysAval = selectedDaysCval
      IsScanningAval = isScanningCval
      ErrorAval = errorCval
      SelectWindow = selectWindow
      Rescan = fun () -> scan selectedDaysCval.Value
      DeleteInvoice = deleteInvoice
      UndeleteInvoice = undeleteInvoice
      LoadProblems = loadProblems
      LoadTombstones = loadTombstones }

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
                | Ok ws -> windowsCval.Value <- ws
                | Error _ -> ()

                match selected with
                | Ok days -> selectedDaysCval.Value <- days
                | Error _ -> ()

                // A failed selected-window read used to be discarded, which left the page marking
                // nothing as "(current)" and saying nothing about why.
                errorCval.Value <-
                    firstFailure [ windows |> Result.map ignore; selected |> Result.map ignore ]

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
            | Error(ex: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false))

    load ()

    { WindowsAval = windowsCval
      SelectedWindowDaysAval = selectedDaysCval
      IsLoadingAval = isLoadingCval
      ErrorAval = errorCval
      Load = load
      AddWindow = fun days -> write (fun () -> scanWindowApi.AddScanWindow days)
      DeleteWindow = fun id -> write (fun () -> scanWindowApi.DeleteScanWindow id) }
