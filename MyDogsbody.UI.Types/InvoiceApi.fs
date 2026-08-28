namespace MyDogsbody.UI.Types

open MyDogsbody.Exceptions.Types

/// The whole surface the invoices page reaches. A record of functions, built by partial
/// application at startup; a test substitutes a record literal.
///
/// Scan returns the scan's result; the getters return the full current view. Writes that change
/// stored data return unit - a write reloads.
type InvoiceApi =
    { /// Run a scan of the given window (days). Rescans on every window change.
      Scan: int -> Result<ScanResultUiType, MyDogsbodyException>
      /// The invoices inside the given window (days), for the table.
      GetInvoices: int -> Result<InvoiceUiType list, MyDogsbodyException>
      /// Delete one invoice by id and write its tombstone.
      DeleteInvoice: string -> Result<unit, MyDogsbodyException>
      /// The persisted problems, for the problems view.
      GetProblems: unit -> Result<ScanProblemUiType list, MyDogsbodyException>
      /// The tombstones, for the tombstones view.
      GetTombstones: unit -> Result<TombstoneUiType list, MyDogsbodyException>
      /// Remove a tombstone (supplier name is not enough - this takes the same natural key the
      /// tombstone row carries), so the next scan restores that invoice.
      UndeleteInvoice: string -> string -> Result<unit, MyDogsbodyException> }

/// The scan-window surface. Consumed by two screens - the settings page that maintains the list
/// and the invoices page's picker - so it is its own record, not part of InvoiceApi.
type ScanWindowApi =
    { GetScanWindows: unit -> Result<ScanWindowUiType list, MyDogsbodyException>
      AddScanWindow: int -> Result<unit, MyDogsbodyException>
      DeleteScanWindow: string -> Result<unit, MyDogsbodyException>
      /// The remembered choice resolved against the current list (never a literal 14).
      GetSelectedScanWindow: unit -> Result<int, MyDogsbodyException>
      SelectScanWindow: int -> Result<unit, MyDogsbodyException> }
