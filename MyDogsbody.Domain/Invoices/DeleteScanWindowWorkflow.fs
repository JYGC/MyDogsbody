/// Deletes a scan window (Q1.17). A seeded window is as deletable as any other; the LAST
/// remaining window is not - CannotDeleteLastScanWindow is a domain rule, not a UI guard, so the
/// picker can never be empty and no component needs an "if the list is empty" branch.
module MyDogsbody.Domain.Invoices.DeleteScanWindowWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices

let deleteScanWindow
    (loadScanWindows: LoadScanWindows)
    (deleteScanWindow: DeleteScanWindow)
    (rawId: string)
    : Result<unit, InvoiceError> =
    result {
        let! windowId =
            ScanWindowId.create rawId |> Result.mapError ScanWindowInvalid

        let! existing = loadScanWindows ()

        if List.length existing <= 1 then
            return! Error CannotDeleteLastScanWindow
        else
            // A window already gone (a concurrent delete) leaves the ledger in the state the
            // user asked for, so it is not an error.
            let! _removed = deleteScanWindow windowId
            return ()
    }
