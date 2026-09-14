module MyDogsbody.Tests.Domain.Calendar.SyncInvoicesToCalendarWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Domain.Calendar.DiffInvoicesAgainstCalendarWorkflow
open MyDogsbody.Domain.Calendar.SyncInvoicesToCalendarWorkflow

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private accountId = GoogleAccountId.create "acct-1" |> orFail
let private calendarId = CalendarId.create "cal-1" |> orFail

let private uploadable (supplier: string) (invoiceReference: string) (dueDate: DateTime) : UploadableInvoice =
    { Id = InvoiceId.create $"inv-{supplier}-{invoiceReference}" |> orFail
      SupplierId = SupplierId.create supplier |> orFail
      Reference = InvoiceReference.create invoiceReference |> orFail
      Amount = Money.create 100m "AUD" |> orFail
      DueDate = InvoiceDueDate.create dueDate |> orFail }

let private eventId value = CalendarEventId.create value |> orFail

/// A fully-recording set of fakes for every dependency `executePlan` takes, so a test can both
/// script each call's answer and assert exactly what was called and how many times.
type private RecordingAdapter() =
    let createCalls = ResizeArray<GoogleAccountId * CalendarId * InvoiceSyncKey * AllDayEvent>()
    let updateCalls = ResizeArray<GoogleAccountId * CalendarId * CalendarEventId * AllDayEvent>()
    let deleteCalls = ResizeArray<GoogleAccountId * CalendarId * CalendarEventId>()
    let markSyncedCalls = ResizeArray<InvoiceId * CalendarEventId>()
    let clearSyncRecordCalls = ResizeArray<CalendarEventId>()

    member val CreateResult: Result<CalendarEventId, CalendarError> = Ok(eventId "new-event") with get, set
    member val UpdateResult: Result<unit, CalendarError> = Ok() with get, set
    member val DeleteResult: Result<unit, CalendarError> = Ok() with get, set

    member _.CreateCalls = createCalls |> List.ofSeq
    member _.UpdateCalls = updateCalls |> List.ofSeq
    member _.DeleteCalls = deleteCalls |> List.ofSeq
    member _.MarkSyncedCalls = markSyncedCalls |> List.ofSeq
    member _.ClearSyncRecordCalls = clearSyncRecordCalls |> List.ofSeq

    member this.CreateCalendarEvent: CreateCalendarEvent =
        fun account calendar key event ->
            createCalls.Add(account, calendar, key, event)
            this.CreateResult

    member this.UpdateCalendarEvent: UpdateCalendarEvent =
        fun account calendar targetEventId event ->
            updateCalls.Add(account, calendar, targetEventId, event)
            this.UpdateResult

    member this.DeleteCalendarEvent: DeleteCalendarEvent =
        fun account calendar targetEventId ->
            deleteCalls.Add(account, calendar, targetEventId)
            this.DeleteResult

    member _.MarkSynced: MarkSynced =
        fun invoiceId account calendar targetEventId ->
            markSyncedCalls.Add(invoiceId, targetEventId)
            Ok()

    member _.ClearSyncRecord: ClearSyncRecord =
        fun targetEventId ->
            clearSyncRecordCalls.Add targetEventId
            Ok()

let private runPlan (adapter: RecordingAdapter) (plan: SyncAction list) =
    executePlan
        adapter.CreateCalendarEvent
        adapter.UpdateCalendarEvent
        adapter.DeleteCalendarEvent
        adapter.MarkSynced
        adapter.ClearSyncRecord
        accountId
        calendarId
        plan

// ================================================================================================
// 4.1
// ================================================================================================

[<Fact; Trait("Level", "Unit")>]
let ``LeaveAlone makes no API call at all`` () =
    let adapter = RecordingAdapter()
    let plan = [ LeaveAlone(eventId "evt-1") ]

    match runPlan adapter plan with
    | Ok [ Skipped id ] -> Assert.Equal(eventId "evt-1", id)
    | other -> Assert.Fail($"Expected Ok [Skipped _], got {other}")

    Assert.Empty(adapter.CreateCalls)
    Assert.Empty(adapter.UpdateCalls)
    Assert.Empty(adapter.DeleteCalls)
    Assert.Empty(adapter.MarkSyncedCalls)
    Assert.Empty(adapter.ClearSyncRecordCalls)

[<Fact; Trait("Level", "Unit")>]
let ``a create stamps the extended property and calls markSynced`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let adapter = RecordingAdapter(CreateResult = Ok(eventId "created-1"))

    match runPlan adapter [ CreateEvent invoice ] with
    | Ok [ Created(invoiceId, createdEventId) ] ->
        Assert.Equal(invoice.Id, invoiceId)
        Assert.Equal(eventId "created-1", createdEventId)
    | other -> Assert.Fail($"Expected Ok [Created _], got {other}")

    match adapter.CreateCalls with
    | [ (account, calendar, key, _) ] ->
        Assert.Equal(accountId, account)
        Assert.Equal(calendarId, calendar)
        Assert.Equal(InvoiceSyncKey.derive invoice.SupplierId invoice.Reference, key)
    | other -> Assert.Fail($"Expected exactly one create call, got {other}")

    Assert.Equal<(InvoiceId * CalendarEventId) list>([ invoice.Id, eventId "created-1" ], adapter.MarkSyncedCalls)

[<Fact; Trait("Level", "Unit")>]
let ``an update rewrites title and date unconditionally`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let adapter = RecordingAdapter()

    match runPlan adapter [ UpdateEvent(eventId "evt-1", invoice) ] with
    | Ok [ Updated(invoiceId, updatedEventId) ] ->
        Assert.Equal(invoice.Id, invoiceId)
        Assert.Equal(eventId "evt-1", updatedEventId)
    | other -> Assert.Fail($"Expected Ok [Updated _], got {other}")

    match adapter.UpdateCalls with
    | [ (_, _, targetEventId, sentEvent) ] ->
        Assert.Equal(eventId "evt-1", targetEventId)
        Assert.Equal(toExpectedEvent invoice, sentEvent)
    | other -> Assert.Fail($"Expected exactly one update call, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a delete calls clearSyncRecord`` () =
    let adapter = RecordingAdapter()
    let key = InvoiceSyncKey.derive (SupplierId.create "sup-1" |> orFail) (InvoiceReference.create "INV-1" |> orFail)

    match runPlan adapter [ DeleteEvent(eventId "evt-1", key) ] with
    | Ok [ Deleted deletedEventId ] -> Assert.Equal(eventId "evt-1", deletedEventId)
    | other -> Assert.Fail($"Expected Ok [Deleted _], got {other}")

    Assert.Equal<CalendarEventId list>([ eventId "evt-1" ], adapter.ClearSyncRecordCalls)

// ================================================================================================
// 4.2 - partial failure and already-gone
// ================================================================================================

[<Fact; Trait("Level", "Unit")>]
let ``a failure mid-batch continues and reports per action, and earlier successes stay recorded`` () =
    let firstInvoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let thirdInvoice = uploadable "sup-3" "INV-3" (DateTime(2026, 3, 12))

    let adapter = RecordingAdapter()
    let mutable callCount = 0

    let scriptedCreate: CreateCalendarEvent =
        fun account calendar key event ->
            callCount <- callCount + 1

            if callCount = 2 then
                Error(EventRejected "invalid title")
            else
                adapter.CreateCalendarEvent account calendar key event

    let plan =
        [ CreateEvent firstInvoice
          CreateEvent(uploadable "sup-2" "INV-2" (DateTime(2026, 3, 11)))
          CreateEvent thirdInvoice ]

    let result =
        executePlan
            scriptedCreate
            adapter.UpdateCalendarEvent
            adapter.DeleteCalendarEvent
            adapter.MarkSynced
            adapter.ClearSyncRecord
            accountId
            calendarId
            plan

    match result with
    | Ok [ Created(firstId, _); Failed(_, EventRejected reason); Created(thirdId, _) ] ->
        Assert.Equal(firstInvoice.Id, firstId)
        Assert.Equal("invalid title", reason)
        Assert.Equal(thirdInvoice.Id, thirdId)
    | other -> Assert.Fail($"Expected [Created; Failed; Created], got {other}")

    // The earlier success (and the later one, since the batch continued) both still recorded.
    Assert.Equal(2, adapter.MarkSyncedCalls.Length)

[<Fact; Trait("Level", "Unit")>]
let ``EventNoLongerExists on an update is AlreadyGone, a success, and the batch continues`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let adapter = RecordingAdapter(UpdateResult = Error(EventNoLongerExists(eventId "evt-1")))

    let plan = [ UpdateEvent(eventId "evt-1", invoice); LeaveAlone(eventId "evt-2") ]

    match runPlan adapter plan with
    | Ok [ AlreadyGone goneId; Skipped keptId ] ->
        Assert.Equal(eventId "evt-1", goneId)
        Assert.Equal(eventId "evt-2", keptId)
    | other -> Assert.Fail($"Expected [AlreadyGone; Skipped], got {other}")

    // No sync-record cleanup on an update-not-found: the invoice is still live, so the next diff
    // will simply see no matching event and produce a fresh create.
    Assert.Empty(adapter.MarkSyncedCalls)
    Assert.Empty(adapter.ClearSyncRecordCalls)

[<Fact; Trait("Level", "Unit")>]
let ``EventNoLongerExists on a delete is AlreadyGone and still clears the sync record`` () =
    let adapter = RecordingAdapter(DeleteResult = Error(EventNoLongerExists(eventId "evt-1")))
    let key = InvoiceSyncKey.derive (SupplierId.create "sup-1" |> orFail) (InvoiceReference.create "INV-1" |> orFail)

    match runPlan adapter [ DeleteEvent(eventId "evt-1", key) ] with
    | Ok [ AlreadyGone goneId ] -> Assert.Equal(eventId "evt-1", goneId)
    | other -> Assert.Fail($"Expected Ok [AlreadyGone _], got {other}")

    Assert.Equal<CalendarEventId list>([ eventId "evt-1" ], adapter.ClearSyncRecordCalls)

[<Fact; Trait("Level", "Unit")>]
let ``CalendarNoLongerExists stops the batch`` () =
    let adapter = RecordingAdapter(CreateResult = Error(CalendarNoLongerExists calendarId))

    let plan =
        [ CreateEvent(uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10)))
          CreateEvent(uploadable "sup-2" "INV-2" (DateTime(2026, 3, 11)))
          LeaveAlone(eventId "evt-3") ]

    match runPlan adapter plan with
    | Ok [ Failed(_, CalendarNoLongerExists reportedCalendarId) ] -> Assert.Equal(calendarId, reportedCalendarId)
    | other -> Assert.Fail($"Expected exactly one Failed(_, CalendarNoLongerExists _) and nothing else, got {other}")

    Assert.Single(adapter.CreateCalls) |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``NotAuthorised mid-batch stops the batch and leaves earlier successes recorded`` () =
    let firstInvoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let adapter = RecordingAdapter()
    let mutable callCount = 0

    let scriptedCreate: CreateCalendarEvent =
        fun account calendar key event ->
            callCount <- callCount + 1

            if callCount = 1 then
                adapter.CreateCalendarEvent account calendar key event
            else
                Error(NotAuthorised accountId)

    let plan =
        [ CreateEvent firstInvoice
          CreateEvent(uploadable "sup-2" "INV-2" (DateTime(2026, 3, 11)))
          CreateEvent(uploadable "sup-3" "INV-3" (DateTime(2026, 3, 12))) ]

    let result =
        executePlan
            scriptedCreate
            adapter.UpdateCalendarEvent
            adapter.DeleteCalendarEvent
            adapter.MarkSynced
            adapter.ClearSyncRecord
            accountId
            calendarId
            plan

    match result with
    | Ok [ Created(firstId, _); Failed(_, NotAuthorised _) ] -> Assert.Equal(firstInvoice.Id, firstId)
    | other -> Assert.Fail($"Expected [Created; Failed(_, NotAuthorised _)], got {other}")

    // The first success is recorded; the third action is never attempted at all.
    Assert.Equal<(InvoiceId * CalendarEventId) list>([ firstInvoice.Id, eventId "new-event" ], adapter.MarkSyncedCalls)

// ================================================================================================
// 4.3 - idempotency, the bar Q2.6 raised
// ================================================================================================

[<Fact; Trait("Level", "Unit")>]
let ``executing a plan and re-deriving one over the result gives all LeaveAlone and zero calls`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let adapter = RecordingAdapter(CreateResult = Ok(eventId "evt-1"))

    let firstPlan = diff { InWindow = [ invoice ]; AllLedgerKeys = Set.ofList [ InvoiceSyncKey.derive invoice.SupplierId invoice.Reference ] } []

    match runPlan adapter firstPlan with
    | Ok [ Created _ ] -> ()
    | other -> Assert.Fail($"Expected Ok [Created _], got {other}")

    // The calendar now holds exactly the event just created - as `listCalendarEvents` would
    // report it on a second read.
    let resultingEvent: CalendarEvent =
        { Id = eventId "evt-1"
          Event = toExpectedEvent invoice
          SyncKey = Some(InvoiceSyncKey.derive invoice.SupplierId invoice.Reference) }

    let secondPlan =
        diff
            { InWindow = [ invoice ]
              AllLedgerKeys = Set.ofList [ InvoiceSyncKey.derive invoice.SupplierId invoice.Reference ] }
            [ resultingEvent ]

    Assert.Equal<SyncAction list>([ LeaveAlone(eventId "evt-1") ], secondPlan)

    let secondAdapter = RecordingAdapter()

    match runPlan secondAdapter secondPlan with
    | Ok [ Skipped _ ] -> ()
    | other -> Assert.Fail($"Expected Ok [Skipped _], got {other}")

    Assert.Empty(secondAdapter.CreateCalls)
    Assert.Empty(secondAdapter.UpdateCalls)
    Assert.Empty(secondAdapter.DeleteCalls)
