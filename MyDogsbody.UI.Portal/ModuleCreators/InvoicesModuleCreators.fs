module MyDogsbody.UI.Portal.ModuleCreators.InvoicesModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

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

    /// Scan the given window and put its result on screen. Also refreshes the window list.
    let scan (days: int) =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            let windows = scanWindowApi.GetScanWindows()
            let result = invoiceApi.Scan days

            transact (fun _ ->
                match windows with
                | Ok ws -> windowsCval.Value <- ws
                | Error _ -> ()

                match result with
                | Ok scanResult ->
                    invoicesCval.Value <- scanResult.Invoices
                    problemsCval.Value <- scanResult.Problems
                    selectedDaysCval.Value <- days
                    errorCval.Value <- None
                | Error(ex: MyDogsbodyException) -> errorCval.Value <- Some ex.Message

                isScanningCval.Value <- false))

    /// The initial load: resolve the remembered window through the API, then scan it.
    let start () =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            match scanWindowApi.GetSelectedScanWindow() with
            | Ok days -> scan days
            | Error(ex: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isScanningCval.Value <- false))

    /// Persist the choice, then rescan.
    let selectWindow (days: int) =
        transact (fun _ -> isScanningCval.Value <- true)

        startWork (fun () ->
            let persisted = scanWindowApi.SelectScanWindow days

            match persisted with
            | Ok() -> scan days
            | Error(ex: MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isScanningCval.Value <- false))

    let deleteInvoice (id: string) =
        startWork (fun () ->
            let result = invoiceApi.DeleteInvoice id
            transact (fun _ -> setError result)

            match result with
            | Ok() -> scan selectedDaysCval.Value
            | Error _ -> ())

    let undeleteInvoice (supplierId: string) (reference: string) =
        startWork (fun () ->
            let result = invoiceApi.UndeleteInvoice supplierId reference
            transact (fun _ -> setError result)

            match result with
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
                | Error(ex: MyDogsbodyException) -> errorCval.Value <- Some ex.Message

                match selected with
                | Ok days -> selectedDaysCval.Value <- days
                | Error _ -> ()

                (match windows with
                 | Ok _ -> errorCval.Value <- None
                 | Error _ -> ())

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
