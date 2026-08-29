namespace MyDogsbody.UI.Types

open MyDogsbody.Exceptions.Types

/// The whole surface the invoices page reaches. A record of functions, built by partial
/// application at startup; a test substitutes a record literal.
///
/// Scan returns the scan's result; the getters return the full current view. Writes that change
/// stored data return unit - a write reloads.
type InvoiceApi =
    { /// Read the mailbox for the given window (days) and return what this scan produced. Called on
      /// the initial load and by the explicit "Scan now" - not on a window change (Q1.9 fallback).
      Scan: int -> Result<ScanResultUiType, MyDogsbodyException>
      /// Discard the selected account's scan watermarks, then read every folder in full for the
      /// given window - "Rescan everything". The escape hatch for when watermarks advanced past
      /// mail that was never turned into an invoice (a folder scanned before a supplier existed).
      RescanEverything: int -> Result<ScanResultUiType, MyDogsbodyException>
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
