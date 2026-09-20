module MyDogsbody.Tests.Startup.InvoiceSyncApiMappersTests

open System
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private orFail =
    function
    | Ok value -> value
    | Error reason -> failwith $"Test setup: {reason}"

let private supplierId value = SupplierId.create value |> orFail
let private reference value = InvoiceReference.create value |> orFail

let private uploadable (supplier: string) (invoiceReference: string) (dueDate: DateTime) : UploadableInvoice =
    { Id = InvoiceId.create $"inv-{supplier}-{invoiceReference}" |> orFail
      SupplierId = supplierId supplier
      Reference = reference invoiceReference
      Amount = Money.create 100m "AUD" |> orFail
      DueDate = InvoiceDueDate.create dueDate |> orFail }

let private keyFor (invoice: UploadableInvoice) : InvoiceSyncKey =
    InvoiceSyncKey.derive invoice.SupplierId invoice.Reference

let private eventId (value: string) = CalendarEventId.create value |> orFail

let private keyedEvent (id: string) (syncKey: InvoiceSyncKey option) : CalendarEvent =
    { Id = eventId id
      Event = { Date = DateTime(2026, 4, 1); Title = $"Invoice due: {id}"; Description = "" }
      SyncKey = syncKey }

// ---------- toOrphanedEvents ----------
//
// "Events the plan does not fully explain" (InvoiceSyncApiMappers.fs's own doc comment on this
// function): a keyless event, a pending-delete event, and - the case this file adds coverage for
// - a second event sharing another event's key, which DiffInvoicesAgainstCalendarWorkflow.diff
// leaves untouched by any SyncAction (its own "Duplicates" comment) and which was therefore
// invisible everywhere in the view before this round's fix.

[<Fact; Trait("Level", "Unit")>]
let ``toOrphanedEvents reports a keyless event as having no recognisable sync key`` () =
    let event = keyedEvent "evt-1" None

    let actual = InvoiceSyncApiMappers.toOrphanedEvents [ event ] []

    match actual with
    | [ row ] ->
        Assert.Equal(event.Event.Title, row.Title)
        Assert.Equal(event.Event.Date, row.Date)
        Assert.Equal(NoRecognisableSyncKey, row.Reason)
    | other -> Assert.Fail($"Expected exactly one orphaned event, got {List.length other}")

[<Fact; Trait("Level", "Unit")>]
let ``toOrphanedEvents reports a pending-delete event as its invoice having left the ledger`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 4, 1))
    let key = keyFor invoice
    let event = keyedEvent "evt-1" (Some key)
    let plan = [ DeleteEvent(event.Id, key) ]

    let actual = InvoiceSyncApiMappers.toOrphanedEvents [ event ] plan

    match actual with
    | [ row ] ->
        Assert.Equal(event.Event.Title, row.Title)
        Assert.Equal(event.Event.Date, row.Date)
        Assert.Equal(InvoiceAlreadyLeftTheLedger, row.Reason)
    | other -> Assert.Fail($"Expected exactly one orphaned event, got {List.length other}")

[<Fact; Trait("Level", "Unit")>]
let ``toOrphanedEvents does not report an event the plan already accounts for`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 4, 1))
    let key = keyFor invoice
    let event = keyedEvent "evt-1" (Some key)

    // One row per plan-action kind that names an event id directly - none of these are orphans.
    [ LeaveAlone event.Id; UpdateEvent(event.Id, invoice) ]
    |> List.iter (fun action ->
        let actual = InvoiceSyncApiMappers.toOrphanedEvents [ event ] [ action ]
        Assert.Empty actual)

[<Fact; Trait("Level", "Unit")>]
let ``toOrphanedEvents reports a second event sharing a key as a duplicate, not as keyless`` () =
    // Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Tests.md - InvoiceSyncApiMappersTests.fs: invoice
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 4, 1))
    let key = keyFor invoice
    let firstEvent = keyedEvent "evt-1" (Some key)
    let duplicateEvent = keyedEvent "evt-2" (Some key)
    // Only the first event is ever named by the plan - exactly what `diff` itself produces when
    // two events share a key.
    let plan = [ LeaveAlone firstEvent.Id ]

    let actual = InvoiceSyncApiMappers.toOrphanedEvents [ firstEvent; duplicateEvent ] plan

    match actual with
    | [ row ] ->
        Assert.Equal(duplicateEvent.Event.Title, row.Title)
        Assert.Equal(duplicateEvent.Event.Date, row.Date)
        Assert.Equal(DuplicateOfAnotherEventsSyncKey, row.Reason)
    | other -> Assert.Fail($"Expected exactly one orphaned (duplicate) event, got {List.length other}")

[<Fact; Trait("Level", "Unit")>]
let ``toOrphanedEvents does not report an event as a duplicate when its invoice is merely outside the current window`` () =
    // Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Tests.md - InvoiceSyncApiMappersTests.fs: invoice (2)
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 4, 1))
    let key = keyFor invoice
    let event = keyedEvent "evt-1" (Some key)
    // Exactly what `diff` produces for a key that is in the ledger but outside the window: no
    // SyncAction names this event at all - not even one for a different invoice.
    let plan: SyncAction list = []

    let actual = InvoiceSyncApiMappers.toOrphanedEvents [ event ] plan

    Assert.Empty actual

// ---------- toSyncPlanRowUiType ----------
//
// PR #23 review round 4: the other three InvoiceSyncApiMappers.fs functions had no dedicated unit
// test of their own (rounds 2 and 3 both re-read them for a hidden defect and found none, but
// left the coverage gap open rather than closing it). Every case is exercised here with every
// output field asserted, per CLAUDE.md's unit-test bar - not merely that a row came back.

[<Fact; Trait("Level", "Unit")>]
let ``toSyncPlanRowUiType maps a CreateEvent action to a full plan row naming the invoice`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 4, 1))
    let names = Map.ofList [ "sup-1", "Acme Corp" ]

    let actual = InvoiceSyncApiMappers.toSyncPlanRowUiType names (CreateEvent invoice)

    match actual with
    | Some row ->
        Assert.Equal(Some(InvoiceId.value invoice.Id), row.InvoiceId)
        Assert.Equal("Acme Corp", row.SupplierName)
        Assert.Equal("INV-1", row.Reference)
        Assert.Equal(InvoiceSyncKey.value (keyFor invoice), row.SyncKey)
        Assert.Equal(Some(InvoiceDueDate.value invoice.DueDate), row.DueDate)
        Assert.Equal(CreateSyncAction, row.Action)
    | None -> Assert.Fail "Expected Some row for CreateEvent"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncPlanRowUiType maps an UpdateEvent action to a full plan row, ignoring the event id it carries`` () =
    let invoice = uploadable "sup-1" "INV-2" (DateTime(2026, 4, 2))
    let names = Map.ofList [ "sup-1", "Acme Corp" ]

    let actual = InvoiceSyncApiMappers.toSyncPlanRowUiType names (UpdateEvent(eventId "evt-1", invoice))

    match actual with
    | Some row ->
        Assert.Equal(Some(InvoiceId.value invoice.Id), row.InvoiceId)
        Assert.Equal("Acme Corp", row.SupplierName)
        Assert.Equal("INV-2", row.Reference)
        Assert.Equal(InvoiceSyncKey.value (keyFor invoice), row.SyncKey)
        Assert.Equal(Some(InvoiceDueDate.value invoice.DueDate), row.DueDate)
        Assert.Equal(UpdateSyncAction, row.Action)
    | None -> Assert.Fail "Expected Some row for UpdateEvent"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncPlanRowUiType maps a DeleteEvent action to a row with no invoice id or due date, named from its sync key`` () =
    // design decision 3: a delete's invoice has already left the ledger by the time diff produces
    // it, so the sync key's own raw parts are the only way left to name which invoice it was.
    let invoice = uploadable "sup-1" "INV-3" (DateTime(2026, 4, 3))
    let key = keyFor invoice
    let names = Map.ofList [ "sup-1", "Acme Corp" ]

    let actual = InvoiceSyncApiMappers.toSyncPlanRowUiType names (DeleteEvent(eventId "evt-1", key))

    match actual with
    | Some row ->
        Assert.Equal(None, row.InvoiceId)
        Assert.Equal("Acme Corp", row.SupplierName)
        Assert.Equal("INV-3", row.Reference)
        Assert.Equal(InvoiceSyncKey.value key, row.SyncKey)
        Assert.Equal(None, row.DueDate)
        Assert.Equal(DeleteSyncAction, row.Action)
    | None -> Assert.Fail "Expected Some row for DeleteEvent"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncPlanRowUiType falls back to a placeholder name for a supplier missing from the names map`` () =
    let invoice = uploadable "sup-404" "INV-1" (DateTime(2026, 4, 1))

    let actual = InvoiceSyncApiMappers.toSyncPlanRowUiType Map.empty (CreateEvent invoice)

    match actual with
    | Some row -> Assert.Equal("(unknown supplier sup-404)", row.SupplierName)
    | None -> Assert.Fail "Expected Some row"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncPlanRowUiType reports no row at all for LeaveAlone`` () =
    // An up-to-date row has nothing to preview - its status lives on InvoiceUiType.SyncStatus via
    // toSyncStatusByInvoiceId instead.
    let actual = InvoiceSyncApiMappers.toSyncPlanRowUiType Map.empty (LeaveAlone(eventId "evt-1"))

    Assert.Equal(None, actual)

// ---------- toSyncStatusByInvoiceId ----------

[<Fact; Trait("Level", "Unit")>]
let ``toSyncStatusByInvoiceId classifies every invoice in the window by its own plan action`` () =
    let missingInvoice = uploadable "sup-1" "INV-MISSING" (DateTime(2026, 4, 1))
    let changedInvoice = uploadable "sup-1" "INV-CHANGED" (DateTime(2026, 4, 2))
    let upToDateInvoice = uploadable "sup-1" "INV-UPTODATE" (DateTime(2026, 4, 3))

    // Exactly the shape diff() itself produces: one of CreateEvent/UpdateEvent/LeaveAlone per
    // invoice in the window - the elimination toSyncStatusByInvoiceId's own doc comment describes.
    let plan =
        [ CreateEvent missingInvoice
          UpdateEvent(eventId "evt-changed", changedInvoice)
          LeaveAlone(eventId "evt-uptodate") ]

    let actual =
        InvoiceSyncApiMappers.toSyncStatusByInvoiceId [ missingInvoice; changedInvoice; upToDateInvoice ] plan

    Assert.Equal<Map<string, InvoiceSyncStatusUiType>>(
        Map.ofList
            [ InvoiceId.value missingInvoice.Id, MissingSync
              InvoiceId.value changedInvoice.Id, ChangedSync
              InvoiceId.value upToDateInvoice.Id, UpToDateSync ],
        actual
    )

[<Fact; Trait("Level", "Unit")>]
let ``toSyncStatusByInvoiceId reports only invoices actually passed as in the window`` () =
    // diff() itself only ever produces a CreateEvent/UpdateEvent for an invoice drawn from its own
    // `snapshot.InWindow` argument, so this can't arise from a real plan - but the mapper's own
    // contract is to report on the `inWindow` argument, not on whatever the plan happens to name,
    // and this is what proves that rather than assuming it from diff's behaviour.
    let inWindowInvoice = uploadable "sup-1" "INV-IN-WINDOW" (DateTime(2026, 4, 1))
    let notInWindowInvoice = uploadable "sup-1" "INV-NOT-IN-WINDOW" (DateTime(2026, 4, 2))
    let plan = [ CreateEvent notInWindowInvoice ]

    let actual = InvoiceSyncApiMappers.toSyncStatusByInvoiceId [ inWindowInvoice ] plan

    Assert.Equal<Map<string, InvoiceSyncStatusUiType>>(
        Map.ofList [ InvoiceId.value inWindowInvoice.Id, UpToDateSync ],
        actual
    )

// ---------- toSyncOutcomeRowUiType ----------
//
// PR #23 review round 6: SyncOutcomeRowUiType carried Reference but not SupplierName, the same
// ambiguity round 1 fixed for ExecuteSyncPlan's own selection matching - the ledger's unique
// index is (supplier, reference), not reference alone, so two different suppliers' outcome rows
// in the same run could show identical Reference text with no way to tell whose succeeded and
// whose failed. Every case below now asserts SupplierName alongside the fields already covered.

[<Fact; Trait("Level", "Unit")>]
let ``toSyncOutcomeRowUiType reports a successful create`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 4, 1))
    let names = Map.ofList [ "sup-1", "Acme Corp" ]
    let action = CreateEvent invoice
    let outcome = Created(invoice.Id, eventId "evt-1")

    let actual = InvoiceSyncApiMappers.toSyncOutcomeRowUiType names (action, outcome)

    match actual with
    | Some row ->
        Assert.Equal("Acme Corp", row.SupplierName)
        Assert.Equal("INV-1", row.Reference)
        Assert.Equal(CreateSyncAction, row.Action)
        Assert.Equal(SyncSucceeded, row.Result)
    | None -> Assert.Fail "Expected Some row for Created"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncOutcomeRowUiType reports a successful update`` () =
    let invoice = uploadable "sup-1" "INV-2" (DateTime(2026, 4, 2))
    let names = Map.ofList [ "sup-1", "Acme Corp" ]
    let action = UpdateEvent(eventId "evt-1", invoice)
    let outcome = Updated(invoice.Id, eventId "evt-1")

    let actual = InvoiceSyncApiMappers.toSyncOutcomeRowUiType names (action, outcome)

    match actual with
    | Some row ->
        Assert.Equal("Acme Corp", row.SupplierName)
        Assert.Equal("INV-2", row.Reference)
        Assert.Equal(UpdateSyncAction, row.Action)
        Assert.Equal(SyncSucceeded, row.Result)
    | None -> Assert.Fail "Expected Some row for Updated"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncOutcomeRowUiType reports a successful delete, naming the invoice and supplier from its sync key`` () =
    let invoice = uploadable "sup-1" "INV-3" (DateTime(2026, 4, 3))
    let names = Map.ofList [ "sup-1", "Acme Corp" ]
    let key = keyFor invoice
    let action = DeleteEvent(eventId "evt-1", key)
    let outcome = Deleted(eventId "evt-1")

    let actual = InvoiceSyncApiMappers.toSyncOutcomeRowUiType names (action, outcome)

    match actual with
    | Some row ->
        Assert.Equal("Acme Corp", row.SupplierName)
        Assert.Equal("INV-3", row.Reference)
        Assert.Equal(DeleteSyncAction, row.Action)
        Assert.Equal(SyncSucceeded, row.Result)
    | None -> Assert.Fail "Expected Some row for Deleted"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncOutcomeRowUiType reports AlreadyGone as a success, not a failure`` () =
    // EventNoLongerExists on an update or delete means the calendar already agrees with the
    // target state - SyncInvoicesToCalendarWorkflow's own AlreadyGone case, never SyncFailed.
    let invoice = uploadable "sup-1" "INV-4" (DateTime(2026, 4, 4))
    let names = Map.ofList [ "sup-1", "Acme Corp" ]
    let action = UpdateEvent(eventId "evt-1", invoice)
    let outcome = AlreadyGone(eventId "evt-1")

    let actual = InvoiceSyncApiMappers.toSyncOutcomeRowUiType names (action, outcome)

    match actual with
    | Some row ->
        Assert.Equal("Acme Corp", row.SupplierName)
        Assert.Equal("INV-4", row.Reference)
        Assert.Equal(UpdateSyncAction, row.Action)
        Assert.Equal(SyncAlreadyGone, row.Result)
    | None -> Assert.Fail "Expected Some row for AlreadyGone"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncOutcomeRowUiType reports a failure carrying the calendar error's own message`` () =
    let invoice = uploadable "sup-1" "INV-5" (DateTime(2026, 4, 5))
    let names = Map.ofList [ "sup-1", "Acme Corp" ]
    let action = CreateEvent invoice
    let outcome = Failed(action, EventRejected "Invalid summary value.")

    let actual = InvoiceSyncApiMappers.toSyncOutcomeRowUiType names (action, outcome)

    match actual with
    | Some row ->
        Assert.Equal("Acme Corp", row.SupplierName)
        Assert.Equal("INV-5", row.Reference)
        Assert.Equal(CreateSyncAction, row.Action)
        Assert.Equal(SyncFailed "Invalid summary value.", row.Result)
    | None -> Assert.Fail "Expected Some row for Failed"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncOutcomeRowUiType falls back to a placeholder supplier name when the names map misses`` () =
    let invoice = uploadable "sup-404" "INV-1" (DateTime(2026, 4, 1))
    let action = CreateEvent invoice
    let outcome = Created(invoice.Id, eventId "evt-1")

    let actual = InvoiceSyncApiMappers.toSyncOutcomeRowUiType Map.empty (action, outcome)

    match actual with
    | Some row -> Assert.Equal("(unknown supplier sup-404)", row.SupplierName)
    | None -> Assert.Fail "Expected Some row"

[<Fact; Trait("Level", "Unit")>]
let ``toSyncOutcomeRowUiType reports nothing for a Skipped (LeaveAlone) outcome`` () =
    // LeaveAlone is never sent to ExecuteSyncPlan in the first place, so it has no outcome row to
    // report - None here is what lets the caller List.choose it away without a placeholder.
    let actual =
        InvoiceSyncApiMappers.toSyncOutcomeRowUiType Map.empty (LeaveAlone(eventId "evt-1"), Skipped(eventId "evt-1"))

    Assert.Equal(None, actual)
