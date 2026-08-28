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
      /// Persist the choice, then rescan - the table shows what was scanned, not what the picker held.
      SelectWindow: int -> unit
      /// Rescan the current window (the explicit Refresh, if task 12.4 turns out to need it).
      Rescan: unit -> unit
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
