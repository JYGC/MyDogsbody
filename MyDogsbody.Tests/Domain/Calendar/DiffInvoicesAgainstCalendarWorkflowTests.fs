module MyDogsbody.Tests.Domain.Calendar.DiffInvoicesAgainstCalendarWorkflowTests

open System
open System.IO
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Domain.Calendar.DiffInvoicesAgainstCalendarWorkflow

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

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

let private eventFor (id: string) (invoice: UploadableInvoice) : CalendarEvent =
    { Id = CalendarEventId.create id |> orFail
      Event = toExpectedEvent invoice
      SyncKey = Some(keyFor invoice) }

let private isDelete =
    function
    | DeleteEvent _ -> true
    | _ -> false

// ================================================================================================
// Phase 2 - the two guards. Write these before trusting anything else in this file: get either
// wrong and this feature can delete a calendar the application neither owns nor can restore.
// ================================================================================================

[<Fact; Trait("Level", "Unit")>]
let ``diff produces no delete for an invoice that is merely outside the window`` () =
    // 180 days of invoices in the ledger; the window has narrowed so InWindow holds only one of
    // them. Events exist for all 180 - including the 179 that are outside the window but very
    // much still in the ledger.
    let allInvoices = [ for day in 1..180 -> uploadable "sup-1" $"INV-{day}" (DateTime(2026, 1, 1).AddDays(float day)) ]
    let recentInvoice = List.head allInvoices
    let allEvents = allInvoices |> List.mapi (fun i invoice -> eventFor $"evt-{i}" invoice)

    let snapshot =
        { InWindow = [ recentInvoice ]
          AllLedgerKeys = allInvoices |> List.map keyFor |> Set.ofList }

    let plan = diff snapshot allEvents

    Assert.Empty(plan |> List.filter isDelete)
    // The 179 out-of-window-but-still-in-the-ledger events must be untouched entirely - not even
    // a LeaveAlone, since diff only ever considers InWindow invoices for anything but delete.
    Assert.Equal(1, plan.Length)

[<Fact; Trait("Level", "Unit")>]
let ``a failed calendar read produces no plan`` () =
    let account: RegisteredGoogleAccount =
        { Id = GoogleAccountId.create "acct-1" |> orFail
          EmailAddress = GoogleEmail.create "someone@example.com" |> orFail
          DefaultInvoiceCalendar = Some(CalendarId.create "cal-1" |> orFail)
          NeedsReauthorisation = false }

    let window = ScanWindowDays.create 14 |> orFail
    let getCurrentTime: GetCurrentTime = fun () -> DateTime(2026, 3, 10)
    let loadInvoices: LoadInvoices = fun _ -> Ok []
    let loadAllLedgerKeys: LoadAllLedgerKeys = fun () -> Ok Set.empty

    let listCalendarEvents: ListCalendarEvents =
        fun _ _ _ -> Error(CalendarUnreachable "token expired")

    match buildPlan getCurrentTime listCalendarEvents loadInvoices loadAllLedgerKeys account window with
    | Error(CalendarUnreachable message) -> Assert.Equal("token expired", message)
    | Ok plan -> Assert.Fail($"Expected Error, but produced a plan of {plan.Length} actions from a failed read")
    | Error other -> Assert.Fail($"Expected CalendarUnreachable, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``Result.defaultValue appears nowhere in the sync workflow files`` () =
    // Hazard (b) has exactly one way in. This closes it at one line: if anyone ever adds a
    // `Result.defaultValue` to a file in this area, this test fails before the code it would
    // introduce could ever ship.
    let repositoryRoot () =
        let rec find (directory: DirectoryInfo) =
            if isNull (box directory) then
                failwith "Could not locate MyDogsbody.sln above the test assembly."
            elif File.Exists(Path.Combine(directory.FullName, "MyDogsbody.sln")) then
                directory.FullName
            else
                find directory.Parent

        find (DirectoryInfo(AppContext.BaseDirectory))

    let calendarFolder = Path.Combine(repositoryRoot (), "MyDogsbody.Domain", "Calendar")

    let offenders =
        Directory.EnumerateFiles(calendarFolder, "*.fs", SearchOption.TopDirectoryOnly)
        |> Seq.filter (fun path -> (File.ReadAllText path).Contains "Result.defaultValue")
        |> Seq.map Path.GetFileName
        |> Seq.toList

    Assert.True(List.isEmpty offenders, $"""Result.defaultValue must never appear in the sync workflow files: {String.Join(", ", offenders)}""")

// ================================================================================================
// Phase 3 - the diff itself.
// ================================================================================================

[<Fact; Trait("Level", "Unit")>]
let ``an invoice with no matching event produces a create`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let snapshot = { InWindow = [ invoice ]; AllLedgerKeys = Set.ofList [ keyFor invoice ] }

    match diff snapshot [] with
    | [ CreateEvent created ] -> Assert.Equal(invoice, created)
    | other -> Assert.Fail($"Expected a single CreateEvent, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a matching event with a different title produces an update`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let event = { eventFor "evt-1" invoice with Event = { toExpectedEvent invoice with Title = "Hand-edited title" } }
    let snapshot = { InWindow = [ invoice ]; AllLedgerKeys = Set.ofList [ keyFor invoice ] }

    match diff snapshot [ event ] with
    | [ UpdateEvent(eventId, updatedInvoice) ] ->
        Assert.Equal(event.Id, eventId)
        Assert.Equal(invoice, updatedInvoice)
    | other -> Assert.Fail($"Expected a single UpdateEvent, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a matching event with a different date produces an update`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let event = { eventFor "evt-1" invoice with Event = { toExpectedEvent invoice with Date = DateTime(2026, 3, 1) } }
    let snapshot = { InWindow = [ invoice ]; AllLedgerKeys = Set.ofList [ keyFor invoice ] }

    match diff snapshot [ event ] with
    | [ UpdateEvent(eventId, _) ] -> Assert.Equal(event.Id, eventId)
    | other -> Assert.Fail($"Expected a single UpdateEvent, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a due date change produces an update, not a delete and a create`` () =
    // Same supplier + reference -> same key, so the invoice's due date moving does not change
    // which event it maps to - the key is derived from supplier + reference alone.
    let oldInvoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 1))
    let newInvoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 15))
    let event = eventFor "evt-1" oldInvoice
    let snapshot = { InWindow = [ newInvoice ]; AllLedgerKeys = Set.ofList [ keyFor newInvoice ] }

    match diff snapshot [ event ] with
    | [ UpdateEvent(eventId, updated) ] ->
        Assert.Equal(event.Id, eventId)
        Assert.Equal(DateTime(2026, 3, 15), InvoiceDueDate.value updated.DueDate)
    | other -> Assert.Fail($"Expected a single UpdateEvent, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``an event and the ledger agreeing produces a leave-alone with no other action`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let event = eventFor "evt-1" invoice
    let snapshot = { InWindow = [ invoice ]; AllLedgerKeys = Set.ofList [ keyFor invoice ] }

    match diff snapshot [ event ] with
    | [ LeaveAlone eventId ] -> Assert.Equal(event.Id, eventId)
    | other -> Assert.Fail($"Expected a single LeaveAlone, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``an event whose key is absent from AllLedgerKeys produces a delete carrying the event id and the sync key`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))
    let event = eventFor "evt-1" invoice
    // The invoice is gone from the WHOLE ledger, not merely outside the window: its key is in
    // neither InWindow nor AllLedgerKeys.
    let snapshot = { InWindow = []; AllLedgerKeys = Set.empty }

    match diff snapshot [ event ] with
    | [ DeleteEvent(eventId, key) ] ->
        Assert.Equal(event.Id, eventId)
        Assert.Equal(keyFor invoice, key)
    | other -> Assert.Fail($"Expected a single DeleteEvent, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``an event with no sync key is never a deletion candidate`` () =
    let handWrittenEvent: CalendarEvent =
        { Id = CalendarEventId.create "evt-hand" |> orFail
          Event = { Date = DateTime(2026, 3, 10); Title = "Dentist"; Description = "" }
          SyncKey = None }

    let snapshot = { InWindow = []; AllLedgerKeys = Set.empty }

    Assert.Empty(diff snapshot [ handWrittenEvent ])

[<Fact; Trait("Level", "Unit")>]
let ``two events for one invoice update the first and neither is deleted`` () =
    let invoice = uploadable "sup-1" "INV-1" (DateTime(2026, 3, 10))

    let firstEvent =
        { eventFor "evt-first" invoice with Event = { toExpectedEvent invoice with Title = "Stale title" } }

    let secondEvent = eventFor "evt-second" invoice
    let snapshot = { InWindow = [ invoice ]; AllLedgerKeys = Set.ofList [ keyFor invoice ] }

    let plan = diff snapshot [ firstEvent; secondEvent ]

    Assert.Empty(plan |> List.filter isDelete)

    match plan with
    | [ UpdateEvent(eventId, _) ] -> Assert.Equal(firstEvent.Id, eventId)
    | other -> Assert.Fail($"Expected a single UpdateEvent against the first event, got {other}")

// ================================================================================================
// Phase 3.3 - buildPlan, the pipeline that binds the read before the pure diff.
// ================================================================================================

let private readyAccount: RegisteredGoogleAccount =
    { Id = GoogleAccountId.create "acct-1" |> orFail
      EmailAddress = GoogleEmail.create "someone@example.com" |> orFail
      DefaultInvoiceCalendar = Some(CalendarId.create "cal-1" |> orFail)
      NeedsReauthorisation = false }

let private notReadyAccount: RegisteredGoogleAccount = { readyAccount with DefaultInvoiceCalendar = None }

let private window = ScanWindowDays.create 14 |> orFail
let private getCurrentTime: GetCurrentTime = fun () -> DateTime(2026, 3, 10)

[<Fact; Trait("Level", "Unit")>]
let ``buildPlan returns NoDefaultCalendar when the account has none, without ever calling listCalendarEvents`` () =
    let mutable wasCalled = false

    let listCalendarEvents: ListCalendarEvents =
        fun _ _ _ ->
            wasCalled <- true
            Ok []

    let loadInvoices: LoadInvoices = fun _ -> Ok []
    let loadAllLedgerKeys: LoadAllLedgerKeys = fun () -> Ok Set.empty

    match buildPlan getCurrentTime listCalendarEvents loadInvoices loadAllLedgerKeys notReadyAccount window with
    | Error(NoDefaultCalendar accountId) -> Assert.Equal(notReadyAccount.Id, accountId)
    | other -> Assert.Fail($"Expected Error (NoDefaultCalendar _), got {other}")

    Assert.False(wasCalled, "listCalendarEvents must never be called when the account has no default calendar")

[<Fact; Trait("Level", "Unit")>]
let ``buildPlan hands listCalendarEvents the range CalendarDateRangeWorkflow derives`` () =
    let dueFarOut = uploadable "sup-1" "INV-1" (DateTime(2026, 5, 9))
    let stored: StoredInvoice =
        { Id = dueFarOut.Id
          ScannedAt = DateTime(2026, 1, 1)
          Invoice =
            { SupplierId = dueFarOut.SupplierId
              TemplateId = TemplateId.create "tpl-1" |> orFail
              SourceMessageId = SourceMessageId.create "msg-1" |> orFail
              Reference = dueFarOut.Reference
              Amount = dueFarOut.Amount
              IssueDate = None
              DueDate = Some dueFarOut.DueDate
              MessageReceivedAt = DateTime(2026, 3, 1) } }

    let mutable capturedRange = None

    let listCalendarEvents: ListCalendarEvents =
        fun _ _ range ->
            capturedRange <- Some range
            Ok []

    let loadInvoices: LoadInvoices = fun _ -> Ok [ stored ]
    let loadAllLedgerKeys: LoadAllLedgerKeys = fun () -> Ok Set.empty

    buildPlan getCurrentTime listCalendarEvents loadInvoices loadAllLedgerKeys readyAccount window
    |> ignore

    let expectedRange = CalendarDateRangeWorkflow.derive getCurrentTime window [ dueFarOut ]

    match capturedRange with
    | Some actualRange -> Assert.Equal(expectedRange, actualRange)
    | None -> Assert.Fail("listCalendarEvents was never called")

[<Fact; Trait("Level", "Unit")>]
let ``buildPlan short-circuits on a failed read rather than producing a plan`` () =
    let loadInvoices: LoadInvoices = fun _ -> Ok []
    let loadAllLedgerKeys: LoadAllLedgerKeys = fun () -> Ok Set.empty
    let listCalendarEvents: ListCalendarEvents = fun _ _ _ -> Error(CalendarUnreachable "boom")

    match buildPlan getCurrentTime listCalendarEvents loadInvoices loadAllLedgerKeys readyAccount window with
    | Error(CalendarUnreachable _) -> ()
    | other -> Assert.Fail($"Expected Error (CalendarUnreachable _), got {other}")
