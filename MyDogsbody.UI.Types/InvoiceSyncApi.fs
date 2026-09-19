namespace MyDogsbody.UI.Types

open MyDogsbody.Exceptions.Types

/// A third API record, separate from InvoiceApi and ScanWindowApi (design decision 6): a surface
/// should not have to take the whole invoice API to do one job.
///
/// ExecuteSyncPlan takes the SELECTED rows (Q2.7): the caller passes the rows a person ticked, or
/// an empty list to act on everything outstanding. It re-derives a fresh plan rather than trusting
/// the rows handed back in, so a stale preview cannot execute an action the ledger or calendar has
/// since moved past.
type InvoiceSyncApi =
    {
        GetSyncPlan: unit -> Result<SyncViewUiType, MyDogsbodyException>
        ExecuteSyncPlan: SyncPlanRowUiType list -> Result<SyncOutcomeRowUiType list, MyDogsbodyException>
    }
