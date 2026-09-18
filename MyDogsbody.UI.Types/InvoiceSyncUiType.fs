namespace MyDogsbody.UI.Types

open System

/// The three per-row sync states a ready invoice can be in (design decision 5). An invoice with
/// no due date carries no sync status at all - InvoiceUiType.CannotUploadReason covers that case
/// instead, and orphaned is not a per-row state because an orphaned event has no invoice row to
/// attach it to (it appears in the plan and in OrphanedEventUiType instead).
type InvoiceSyncStatusUiType =
    | UpToDateSync
    | MissingSync
    | ChangedSync

/// What kind of action a plan row or outcome row is.
type SyncPlanActionUiType =
    | CreateSyncAction
    | UpdateSyncAction
    | DeleteSyncAction

/// One action the plan would take, shown before anything runs (Q2.13) - naming the invoice, not
/// just an event id (design decision 3). InvoiceId and DueDate are None only for a delete: the
/// invoice itself has already left the ledger, which is the whole reason it is a delete.
///
/// SyncKey carries the raw InvoiceSyncKey string (supplier + reference, InvoiceSyncKey.value) -
/// not shown on screen, but the ONLY field ExecuteSyncPlan may use to tell one row's action from
/// another's. Reference alone is not unique: the ledger's index is on (supplier, reference), not
/// on reference alone, so two different suppliers can share the same reference text. Matching a
/// ticked selection back to plan actions by Reference let a tick on one supplier's row also fire
/// a different supplier's action for the same reference text - PR #23 review round 1.
type SyncPlanRowUiType =
    {
        InvoiceId: string option
        SupplierName: string
        Reference: string
        SyncKey: string
        DueDate: DateTime option
        Action: SyncPlanActionUiType
    }

/// The outcome of executing one row of the plan (Q2.8).
type SyncOutcomeResultUiType =
    | SyncSucceeded
    /// The calendar already agreed with the target state (EventNoLongerExists) - a success, not
    /// a failure.
    | SyncAlreadyGone
    | SyncFailed of message: string

type SyncOutcomeRowUiType =
    {
        Reference: string
        Action: SyncPlanActionUiType
        Result: SyncOutcomeResultUiType
    }

/// An event on the calendar the plan does not fully explain: either its invoice has left the
/// ledger (also shown as a pending delete in the plan) or it carries no sync key, or an
/// unparseable one - added by hand, or corrupted. NeedsAttention is true only for the latter:
/// the app deletes only what it can prove it created, so a keyless event is never a deletion
/// candidate and a person has to decide what to do with it.
type OrphanedEventUiType =
    {
        Title: string
        Date: DateTime
        NeedsAttention: bool
    }

/// The whole picture the invoices page needs to render calendar sync: every ready invoice's
/// status, the plan of outstanding actions, and events the plan does not explain.
///
/// NotReadyReason is Some when no Google account is ready to sync to at all - none registered,
/// or the registered one has no default calendar chosen (Q2.11) - in which case Plan and
/// OrphanedEvents are empty and the bulk button is disabled with this sentence.
type SyncViewUiType =
    {
        StatusByInvoiceId: Map<string, InvoiceSyncStatusUiType>
        Plan: SyncPlanRowUiType list
        OrphanedEvents: OrphanedEventUiType list
        NotReadyReason: string option
    }
