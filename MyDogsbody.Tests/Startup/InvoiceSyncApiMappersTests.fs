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
    // requirements.md's edge case: "WHEN the same invoice has two events on the calendar THE
    // SYSTEM SHALL update the first and report the second as a duplicate, and SHALL NOT delete it
    // without confirmation." `diff` compares only the first-seen event for a shared key against
    // the ledger (its own "Duplicates" comment) and leaves every other event untouched - neither
    // updated nor deleted, so it is never the target of any SyncAction. Before round 2's fix, that
    // made the second event invisible here too. Before round 3's fix, it was visible but
    // mislabelled: OrphanedEventUiType's old bare `NeedsAttention: bool` could not tell this case
    // apart from a genuinely keyless event, so the screen told the user "no recognisable sync key"
    // for an event that had one - just not the one `diff` matched. This asserts the exact reason,
    // not merely that the row is flagged.
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
