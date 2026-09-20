/// The order the picker renders them in.
module MyDogsbody.Domain.Invoices.ListScanWindowsWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices

let listScanWindows (loadScanWindows: LoadScanWindows) () : Result<StoredScanWindow list, InvoiceError> =
    result {
        let! windows = loadScanWindows ()
        let ascendingByDayCount = List.sortBy (fun window -> ScanWindowDays.value window.Days)
        return windows |> ascendingByDayCount
    }
