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

type SyncPlanActionUiType =
    | CreateSyncAction
    | UpdateSyncAction
    | DeleteSyncAction

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.UI.Types.md - InvoiceSyncUiType.fs: SyncPlanRowUiType
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

/// SupplierName alongside Reference, mirroring SyncPlanRowUiType, and for the same reason
/// (PR #23 review round 1's own finding, applied here in round 6): the ledger's unique index is
/// (supplier, reference), not reference alone, so two different suppliers can share the same
/// reference text. Without SupplierName, two outcome rows in the same run could show identical
/// Reference text with no way to tell whose succeeded and whose failed.
type SyncOutcomeRowUiType =
    {
        SupplierName: string
        Reference: string
        Action: SyncPlanActionUiType
        Result: SyncOutcomeResultUiType
    }

/// Why an event the sync plan does not fully explain is being shown (PR #23 review round 3). A
/// bare bool could not tell these three apart, and telling the user "no recognisable sync key" for
/// the third case was actively false - it has one, just not the one `diff` matched to an invoice.
/// requirements.md's own wording for that case is specific: "report the second as a duplicate".
type OrphanedEventReasonUiType =
    /// No sync key at all, or one that failed to parse - added by hand, or corrupted. Never a
    /// deletion candidate: the app deletes only what it can prove it created.
    | NoRecognisableSyncKey
    /// A second event sharing another event's sync key (requirements.md: "the same invoice has two
    /// events on the calendar"). `diff` updates the first and leaves this one untouched - neither
    /// updated nor deleted - so a person still has to decide what to do with it, but for a reason
    /// worth naming accurately rather than folding into the keyless case above.
    | DuplicateOfAnotherEventsSyncKey
    /// Its invoice has already left the ledger - already shown as a pending delete in the plan
    /// above. Nothing for a person to decide here.
    | InvoiceAlreadyLeftTheLedger

/// An event on the calendar the plan does not fully explain: either its invoice has left the
/// ledger (also shown as a pending delete in the plan), it carries no sync key or an unparseable
/// one - added by hand, or corrupted - or it duplicates another event's sync key.
type OrphanedEventUiType =
    {
        Title: string
        Date: DateTime
        Reason: OrphanedEventReasonUiType
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
