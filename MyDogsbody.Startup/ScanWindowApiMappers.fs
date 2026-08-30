/// The top mapping point for scan windows: StoredScanWindow -> ScanWindowUiType, with the Label
/// composed HERE, not by the component (Q1.6 - the label says what the window measures).
///
/// InvoiceError translation is shared with the ledger - InvoiceApiMappers.toMyDogsbodyException.
module MyDogsbody.Startup.ScanWindowApiMappers

open MyDogsbody.Domain.Invoices
open MyDogsbody.UI.Types

/// "mail received in the last 90 days" - never a bare "90". The window is measured on the date
/// the mail ARRIVED, and a bare number is exactly where someone assumes it means due dates.
let windowLabel (days: int) : string =
    match days with
    | 1 -> "mail received in the last day"
    | n -> $"mail received in the last {n} days"

let toUiType (window: StoredScanWindow) : ScanWindowUiType =
    let days = ScanWindowDays.value window.Days

    { Id = ScanWindowId.value window.Id
      Days = days
      Label = windowLabel days }
