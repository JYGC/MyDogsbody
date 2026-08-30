/// Lists the scan windows the store holds, ascending by day count - the order the picker
/// renders them in.
module MyDogsbody.Domain.Invoices.ListScanWindowsWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices

let listScanWindows (loadScanWindows: LoadScanWindows) () : Result<StoredScanWindow list, InvoiceError> =
    result {
        let! windows = loadScanWindows ()
        return windows |> List.sortBy (fun window -> ScanWindowDays.value window.Days)
    }
