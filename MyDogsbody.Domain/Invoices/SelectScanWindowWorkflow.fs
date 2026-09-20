/// Q1.7 - the choice persists, as a NUMBER of days, in the main database.
module MyDogsbody.Domain.Invoices.SelectScanWindowWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices

let selectScanWindow
    (loadScanWindows: LoadScanWindows)
    (saveSelectedScanWindow: SaveSelectedScanWindow)
    (rawDays: int)
    : Result<ScanWindowDays, InvoiceError> =
    result {
        let! windows = loadScanWindows ()

        let storedWindowWithTheRequestedDayCount =
            windows
            |> List.tryFind (fun window -> ScanWindowDays.value window.Days = rawDays)

        match storedWindowWithTheRequestedDayCount with
        | None -> return! Error(ScanWindowNotFound rawDays)
        | Some window ->
            do! saveSelectedScanWindow window.Days
            return window.Days
    }
