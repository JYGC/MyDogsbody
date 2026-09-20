/// The top mapping point for calendar sync: domain type <-> MyDogsbody.UI.Types record.
///
/// Total functions, no module-level bindings.
module MyDogsbody.Startup.InvoiceSyncApiMappers

open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.UI.Types

let private supplierName (namesById: Map<string, string>) (id: SupplierId) : string =
    namesById
    |> Map.tryFind (SupplierId.value id)
    |> Option.defaultValue $"(unknown supplier {SupplierId.value id})"

/// Reads a supplier's name back from an InvoiceSyncKey's raw parts - the only way to say which
/// invoice a DeleteEvent's event belonged to (design decision 3), since the invoice itself has
/// already left the ledger by the time a delete is produced.
let private nameAndReferenceFromKey (namesById: Map<string, string>) (key: InvoiceSyncKey) : string * string =
    match InvoiceSyncKey.supplierRowIdAndInvoiceReferenceAsPlainStrings key with
    | Some(rawSupplierId, reference) ->
        let name =
            match SupplierId.create rawSupplierId with
            | Ok supplierId -> supplierName namesById supplierId
            | Error _ -> $"(unknown supplier {rawSupplierId})"

        name, reference
    | None -> "(unknown supplier)", "(unknown reference)"

/// The raw InvoiceSyncKey string for an invoice still in the ledger - the one unambiguous key a
/// create or update row can carry (PR #23 review round 1). Derived, never hand-built, so it agrees
/// with the key `diff` itself keyed the action by.
let private invoiceSyncKeyValue (invoice: UploadableInvoice) : string =
    InvoiceSyncKey.derive invoice.SupplierId invoice.Reference |> InvoiceSyncKey.value

/// A SyncAction -> a UI plan row, naming the invoice rather than just an event id (design
/// decision 3). `None` for `LeaveAlone`: an up-to-date row has nothing to preview, and its status
/// is carried on `InvoiceUiType.SyncStatus` instead - see `toSyncStatusByInvoiceId` below.
let toSyncPlanRowUiType (namesById: Map<string, string>) (action: SyncAction) : SyncPlanRowUiType option =
    match action with
    | CreateEvent invoice ->
        Some
            {
                InvoiceId = Some(InvoiceId.value invoice.Id)
                SupplierName = supplierName namesById invoice.SupplierId
                Reference = InvoiceReference.value invoice.Reference
                SyncKey = invoiceSyncKeyValue invoice
                DueDate = Some(InvoiceDueDate.value invoice.DueDate)
                Action = CreateSyncAction
            }
    | UpdateEvent(_, invoice) ->
        Some
            {
                InvoiceId = Some(InvoiceId.value invoice.Id)
                SupplierName = supplierName namesById invoice.SupplierId
                Reference = InvoiceReference.value invoice.Reference
                SyncKey = invoiceSyncKeyValue invoice
                DueDate = Some(InvoiceDueDate.value invoice.DueDate)
                Action = UpdateSyncAction
            }
    | DeleteEvent(_, key) ->
        let name, reference = nameAndReferenceFromKey namesById key

        Some
            {
                InvoiceId = None
                SupplierName = name
                Reference = reference
                SyncKey = InvoiceSyncKey.value key
                DueDate = None
                Action = DeleteSyncAction
            }
    | LeaveAlone _ -> None

/// Every invoice in view classified as up to date, missing or changed (design decision 5) - by
/// elimination over the plan's Create/Update actions, which both carry the invoice, rather than
/// by any assumption about `diff`'s internal ordering: an invoice not named by either action is
/// necessarily the LeaveAlone case, since `diff` produces exactly one of the three for every
/// invoice in `InWindow`.
let toSyncStatusByInvoiceId (inWindow: UploadableInvoice list) (plan: SyncAction list) : Map<string, InvoiceSyncStatusUiType> =
    let missingIds =
        plan
        |> List.choose (function
            | CreateEvent invoice -> Some(InvoiceId.value invoice.Id)
            | _ -> None)
        |> Set.ofList

    let changedIds =
        plan
        |> List.choose (function
            | UpdateEvent(_, invoice) -> Some(InvoiceId.value invoice.Id)
            | _ -> None)
        |> Set.ofList

    inWindow
    |> List.map (fun invoice ->
        let id = InvoiceId.value invoice.Id

        let status =
            if Set.contains id missingIds then MissingSync
            elif Set.contains id changedIds then ChangedSync
            else UpToDateSync

        id, status)
    |> Map.ofList

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Startup.md - InvoiceSyncApiMappers.fs: toOrphanedEvents
let toOrphanedEvents (events: CalendarEvent list) (plan: SyncAction list) : OrphanedEventUiType list =
    let deletedEventIds =
        plan
        |> List.choose (function
            | DeleteEvent(eventId, _) -> Some eventId
            | _ -> None)
        |> Set.ofList

    /// Every event id the plan names as the target of an update, a leave-alone or a delete - the
    /// canonical (first-seen) event for its key, whichever action `diff` gave it. A CreateEvent
    /// names no event id at all, since none exists yet.
    let matchedEventIds =
        plan
        |> List.choose (function
            | UpdateEvent(eventId, _)
            | LeaveAlone eventId
            | DeleteEvent(eventId, _) -> Some eventId
            | CreateEvent _ -> None)
        |> Set.ofList

    /// Every sync key the plan explains via SOME matched event - not necessarily the one being
    /// classified. An unmatched event whose own key appears here is a genuine second event for an
    /// already-claimed key; an unmatched event whose key does NOT appear here belongs to an
    /// invoice the plan never looked at at all (out of window), not a duplicate.
    let explainedKeys =
        events
        |> List.choose (fun event -> if Set.contains event.Id matchedEventIds then event.SyncKey else None)
        |> Set.ofList

    events
    |> List.choose (fun event ->
        match event.SyncKey with
        | None -> Some { Title = event.Event.Title; Date = event.Event.Date; Reason = NoRecognisableSyncKey }
        | Some _ when Set.contains event.Id deletedEventIds ->
            Some { Title = event.Event.Title; Date = event.Event.Date; Reason = InvoiceAlreadyLeftTheLedger }
        | Some key when not (Set.contains event.Id matchedEventIds) && Set.contains key explainedKeys ->
            // Keyed, not the one `diff` matched to its invoice, and that key IS accounted for
            // elsewhere in the plan - a duplicate, per requirements.md's own wording for this edge
            // case (PR #23 review round 3).
            Some { Title = event.Event.Title; Date = event.Event.Date; Reason = DuplicateOfAnotherEventsSyncKey }
        | Some _ -> None)

/// One executed SyncAction and its SyncOutcome -> a UI outcome row, naming the invoice the same
/// way toSyncPlanRowUiType does - SupplierName alongside Reference, for the same reason
/// toSyncPlanRowUiType carries both (PR #23 review round 1): the ledger's unique index is
/// (supplier, reference), not reference alone, so two different suppliers' rows can show
/// identical reference text, and a Reference-only outcome row could not tell whose succeeded and
/// whose failed when both had an action in the same run (PR #23 review round 6). `None` for
/// `Skipped`: a LeaveAlone is never sent to ExecuteSyncPlan in the first place, so it never has an
/// outcome to report.
let toSyncOutcomeRowUiType (namesById: Map<string, string>) (action: SyncAction, outcome: SyncOutcome) : SyncOutcomeRowUiType option =
    let outcomeSupplierName =
        match action with
        | CreateEvent invoice
        | UpdateEvent(_, invoice) -> supplierName namesById invoice.SupplierId
        | DeleteEvent(_, key) -> nameAndReferenceFromKey namesById key |> fst
        | LeaveAlone _ -> "(unknown supplier)"

    let reference =
        match action with
        | CreateEvent invoice
        | UpdateEvent(_, invoice) -> InvoiceReference.value invoice.Reference
        | DeleteEvent(_, key) -> nameAndReferenceFromKey namesById key |> snd
        | LeaveAlone _ -> "(unknown reference)"

    let actionKind =
        match action with
        | CreateEvent _ -> CreateSyncAction
        | UpdateEvent _ -> UpdateSyncAction
        | DeleteEvent _ -> DeleteSyncAction
        | LeaveAlone _ -> CreateSyncAction // unreachable - see the Skipped case below

    // Reuses GoogleAccountApiMappers.toMyDogsbodyException for its message text rather than a
    // second CalendarError -> string translation - the action name is irrelevant here, only the
    // sentence a person reads for this one failed row.
    let calendarErrorMessage (error: CalendarError) : string =
        (GoogleAccountApiMappers.toMyDogsbodyException "InvoiceSyncApiMappers.calendarErrorMessage" error).Message

    match outcome with
    | Created _
    | Updated _
    | Deleted _ ->
        Some { SupplierName = outcomeSupplierName; Reference = reference; Action = actionKind; Result = SyncSucceeded }
    | AlreadyGone _ ->
        Some { SupplierName = outcomeSupplierName; Reference = reference; Action = actionKind; Result = SyncAlreadyGone }
    | Failed(_, error) ->
        Some
            { SupplierName = outcomeSupplierName
              Reference = reference
              Action = actionKind
              Result = SyncFailed(calendarErrorMessage error) }
    | Skipped _ -> None
