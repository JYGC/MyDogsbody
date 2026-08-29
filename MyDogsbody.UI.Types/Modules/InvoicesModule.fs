namespace MyDogsbody.UI.Types.Module

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types

/// The invoices page's adaptive state. The window picker renders whatever ScanWindowsAval holds
/// (never a list of its own); the picker opens on SelectedWindowDaysAval, which is the resolved
/// remembered choice from the API, never a literal.
type InvoicesModule =
    { InvoicesAval: aval<InvoiceUiType list>
      ProblemsAval: aval<ScanProblemUiType list>
      TombstonesAval: aval<TombstoneUiType list>
      ScanWindowsAval: aval<ScanWindowUiType list>
      SelectedWindowDaysAval: aval<int>
      IsScanningAval: aval<bool>
      /// The message from the last failed operation, cleared by the next successful one.
      ErrorAval: aval<string option>
      /// Persist the choice and reload the stored ledger for it. NOT a mailbox scan - task 12.4
      /// measured that at ~60 s whatever the window, so a window change filters, it does not rescan.
      SelectWindow: int -> unit
      /// Read the mailbox for the current window and refresh the ledger - the explicit "Scan now",
      /// the only path that reads mail after the initial load (Q1.9 fallback, settled by 12.4).
      Rescan: unit -> unit
      /// Discard the selected account's watermarks and read every folder in full for the current
      /// window - "Rescan everything". For mail that an ordinary scan resumes straight past
      /// because the folder was read before its supplier or template existed.
      RescanEverything: unit -> unit
      DeleteInvoice: string -> unit
      /// supplierId, reference - the natural key the tombstone row carries.
      UndeleteInvoice: string -> string -> unit
      LoadProblems: unit -> unit
      LoadTombstones: unit -> unit }

/// The /settings/scan-windows page's adaptive state.
type ScanWindowsBrowserModule =
    { WindowsAval: aval<ScanWindowUiType list>
      SelectedWindowDaysAval: aval<int>
      IsLoadingAval: aval<bool>
      ErrorAval: aval<string option>
      Load: unit -> unit
      AddWindow: int -> unit
      DeleteWindow: string -> unit }
