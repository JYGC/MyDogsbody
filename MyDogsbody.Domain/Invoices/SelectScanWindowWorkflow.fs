/// Persists the user's scan-window choice (Q1.7 - the choice persists, as a NUMBER of days, in
/// the main database). Refuses a day count that is not one of the stored windows, and does not
/// touch the store on that refusal.
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

        let chosen =
            windows
            |> List.tryFind (fun window -> ScanWindowDays.value window.Days = rawDays)

        match chosen with
        | None -> return! Error(ScanWindowNotFound rawDays)
        | Some window ->
            do! saveSelectedScanWindow window.Days
            return window.Days
    }
