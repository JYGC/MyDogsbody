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
      LoadTombstones: unit -> unit

      // --- change #7: calendar sync ---
      SyncViewAval: aval<SyncViewUiType>
      IsSyncingAval: aval<bool>
      LoadSyncPlan: unit -> unit
      /// The rows a person has ticked, by invoice id - view state only, never persisted (task
      /// 8.2). A rescan clears it: a tick against a row that no longer exists is worse than no
      /// tick at all.
      SelectedInvoiceIdsAval: aval<Set<string>>
      ToggleInvoice: string -> unit
      ClearSelection: unit -> unit
      /// The count the bulk button states before it is pressed - the selection's outstanding
      /// actions when there is a selection, everything outstanding in the plan when there is not
      /// (Q2.7).
      PendingActionCountAval: aval<int>
      /// Runs ExecuteSyncPlan over the current selection (or everything outstanding, if none is
      /// selected) and reloads the plan on completion.
      ExecuteSync: unit -> unit
      /// The per-row result of the most recent ExecuteSync run (task 8.7) - empty before the first
      /// run. ExecuteSync is `unit -> unit`, so this is the only channel back to the screen for what
      /// happened; added alongside the rest of the change #7 UI state rather than left out, since
      /// without it a partial failure (Q2.8) would run with no way to show which rows failed and why.
      LastSyncOutcomesAval: aval<SyncOutcomeRowUiType list> }

/// The /settings/scan-windows page's adaptive state.
type ScanWindowsBrowserModule =
    { WindowsAval: aval<ScanWindowUiType list>
      SelectedWindowDaysAval: aval<int>
      IsLoadingAval: aval<bool>
      ErrorAval: aval<string option>
      Load: unit -> unit
      AddWindow: int -> unit
      DeleteWindow: string -> unit }
