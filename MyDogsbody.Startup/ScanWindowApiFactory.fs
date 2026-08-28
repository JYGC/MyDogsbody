/// The scan-window composition: ScanWindowStore adapters, the four scan-window workflows plus
/// ResolveScanWindowWorkflow, and the InvoiceError -> MyDogsbodyException translation (shared
/// with the ledger via InvoiceApiMappers).
module MyDogsbody.Startup.ScanWindowApiFactory

open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database
open MyDogsbody.UI.Types

let createScanWindowApi (handleError: HandleErrorBuilder) (databaseContext: DatabaseContext) : ScanWindowApi =
    let conn = databaseContext.GetDatabaseConnection
    let toInvoiceError = InvoiceApiMappers.toInvoiceError

    let loadScanWindows: LoadScanWindows =
        fun () ->
            ScanWindowStore.getScanWindows handleError conn databaseContext.GetScanWindows ()
            |> Result.mapError toInvoiceError

    let saveScanWindow: SaveScanWindow =
        fun days -> ScanWindowStore.saveScanWindow handleError conn days |> Result.mapError toInvoiceError

    let deleteScanWindowDep: DeleteScanWindow =
        fun id -> ScanWindowStore.deleteScanWindow handleError conn id |> Result.mapError toInvoiceError

    let loadSelectedScanWindow: LoadSelectedScanWindow =
        fun () -> ScanWindowStore.getSelectedScanWindow handleError conn () |> Result.mapError toInvoiceError

    let saveSelectedScanWindow: SaveSelectedScanWindow =
        fun days -> ScanWindowStore.saveSelectedScanWindow handleError conn days |> Result.mapError toInvoiceError

    let toException = InvoiceApiMappers.toMyDogsbodyException

    { GetScanWindows =
        fun () ->
            ListScanWindowsWorkflow.listScanWindows loadScanWindows ()
            |> Result.map (List.map ScanWindowApiMappers.toUiType)
            |> Result.mapError (toException ActionNames.MyDogsbody.Startup.ScanWindowApi.getScanWindows)

      AddScanWindow =
        fun days ->
            AddScanWindowWorkflow.addScanWindow loadScanWindows saveScanWindow days
            |> Result.map ignore
            |> Result.mapError (toException ActionNames.MyDogsbody.Startup.ScanWindowApi.addScanWindow)

      DeleteScanWindow =
        fun rawId ->
            DeleteScanWindowWorkflow.deleteScanWindow loadScanWindows deleteScanWindowDep rawId
            |> Result.mapError (toException ActionNames.MyDogsbody.Startup.ScanWindowApi.deleteScanWindow)

      GetSelectedScanWindow =
        fun () ->
            result {
                let! windows = loadScanWindows ()
                let! remembered = loadSelectedScanWindow ()
                return ScanWindowDays.value (ResolveScanWindowWorkflow.resolveScanWindow windows remembered)
            }
            |> Result.mapError (toException ActionNames.MyDogsbody.Startup.ScanWindowApi.getSelectedScanWindow)

      SelectScanWindow =
        fun days ->
            SelectScanWindowWorkflow.selectScanWindow loadScanWindows saveSelectedScanWindow days
            |> Result.map ignore
            |> Result.mapError (toException ActionNames.MyDogsbody.Startup.ScanWindowApi.selectScanWindow) }
