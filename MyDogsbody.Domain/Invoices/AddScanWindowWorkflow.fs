/// Q1.17: add and delete, no edit.
module MyDogsbody.Domain.Invoices.AddScanWindowWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices

let addScanWindow
    (loadScanWindows: LoadScanWindows)
    (saveScanWindow: SaveScanWindow)
    (rawDays: int)
    : Result<StoredScanWindow, InvoiceError> =
    result {
        let! days =
            ScanWindowDays.create rawDays |> Result.mapError ScanWindowInvalid

        let! existing = loadScanWindows ()

        let alreadyPresent =
            existing
            |> List.exists (fun window -> ScanWindowDays.value window.Days = rawDays)

        if alreadyPresent then
            return! Error(ScanWindowAlreadyExists rawDays)
        else
            return! saveScanWindow days
    }
