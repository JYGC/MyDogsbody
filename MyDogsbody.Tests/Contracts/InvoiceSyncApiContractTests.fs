module MyDogsbody.Tests.Contracts.InvoiceSyncApiContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Domain.Calendar.DiffInvoicesAgainstCalendarWorkflow
open MyDogsbody.Domain.Calendar.SyncInvoicesToCalendarWorkflow
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

/// InvoiceSyncApi is a published interface (GetSyncPlan, ExecuteSyncPlan), so - per CLAUDE.md's
/// contract rule - one suite is meant to run against the real record and against the in-memory fake
/// the UI's own tests use, the same shape as SupplierApiContractTests.fs.
///
/// A genuine gap found while writing this suite, not assumed going in: unlike GoogleAccountApiFactory
/// (bindListCalendarEvents and its siblings all take the Google-facing call as a parameter, which is
/// exactly what lets ListCalendarEventsDependencyContractTests.fs and its three siblings run the real
/// composition over a stubbed HttpMessageHandler), InvoiceSyncApiFactory.createInvoiceSyncApi wires
/// GoogleCalendarClient.listEvents/createEvent/updateEvent/deleteEvent - the production, unstubbable
/// entry points - directly, with no HttpClientFactory seam in its own signature. So the real
/// InvoiceSyncApi can only be exercised here for the paths that never reach Google: no account ready
/// to sync to at all, and a ready account with no stored authorisation (GoogleAuthorization.loadCredential
/// fails locally, before a single byte would go to Google). Any scenario that needs a successful or
/// stubbed Google read/write - an outstanding plan, executing it - cannot be run against the real
/// record without either a live network call (forbidden by this whole change's standing rule) or a
/// change to InvoiceSyncApiFactory.fs (out of scope for this task, which owns only MyDogsbody.Tests).
/// Those scenarios are covered against the fake only, clearly marked below; closing the gap on the
/// real side is left as a follow-up for whoever next touches InvoiceSyncApiFactory.fs.

let private handleError = HandleErrorBuilder(fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private getCurrentTime () = DateTime(2026, 6, 1, 9, 0, 0)

// ---------- the real API, over a temp SQLite file and a temp Google.db ----------

let private withRealApi (test: InvoiceSyncApi -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Pooling=False"
    MigrationSetup.setupMigrations connectionString
    let databaseContext = DatabaseContextSetup.createDatabaseContext databaseFilePath

    let googleDatabasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let googleContext = GoogleDatabaseContextModule.getDatabaseContext googleDatabasePath "direct"

    try
        test (InvoiceSyncApiFactory.createInvoiceSyncApi handleError getCurrentTime databaseContext googleContext)
    finally
        databaseContext.Dispose()
        googleContext.Dispose()
        try File.Delete databaseFilePath with _ -> ()
        try File.Delete googleDatabasePath with _ -> ()

/// Same as `withRealApi`, but with one Google account registered and ready (a default invoice
/// calendar chosen) - with no token stored for it, so `GoogleAuthorization.loadCredential` fails
/// before anything would reach Google.
let private withRealApiReadyButUnauthorised (test: InvoiceSyncApi -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Pooling=False"
    MigrationSetup.setupMigrations connectionString
    let databaseContext = DatabaseContextSetup.createDatabaseContext databaseFilePath

    let googleDatabasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let googleContext = GoogleDatabaseContextModule.getDatabaseContext googleDatabasePath "direct"

    try
        let sampleClientSecret =
            """{ "installed": { "client_id": "test-client-id", "client_secret": "test-client-secret" } }"""

        match GoogleAccountStore.saveClientSecret handleError googleContext.GetClientSecretCollection sampleClientSecret with
        | Ok() -> ()
        | Error e -> failwith $"Test setup could not store the client secret: {e.Message}"

        let account: RegisteredGoogleAccount =
            { Id = GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail
              EmailAddress = GoogleEmail.create "someone@example.com" |> valueOrFail
              DefaultInvoiceCalendar = Some(CalendarId.create "cal-1" |> valueOrFail)
              NeedsReauthorisation = false }

        match GoogleAccountStore.saveOne handleError googleContext.GetAccountCollection account with
        | Ok _ -> ()
        | Error e -> failwith $"Test setup could not store the account: {e.Message}"

        test (InvoiceSyncApiFactory.createInvoiceSyncApi handleError getCurrentTime databaseContext googleContext)
    finally
        databaseContext.Dispose()
        googleContext.Dispose()
        try File.Delete databaseFilePath with _ -> ()
        try File.Delete googleDatabasePath with _ -> ()

// ---------- the in-memory fake, composed from the real pure workflows (diff, executePlan) and the
// real top mapper (InvoiceSyncApiMappers) over fake storage/Google state - the same idiom
// SupplierApiContractTests.fs's withFakeApi uses: a fake mirrors the composition root's own shape,
// kept faithful to its behaviour, with only I/O replaced. ----------

let private notReadyMessage =
    "No Google account is ready to sync to yet. Register one on the Google accounts page and choose its default invoice calendar."

let private withFakeApi
    (readyAccount: (GoogleAccountId * CalendarId) option)
    (invoicesInWindow: UploadableInvoice list)
    (supplierNamesById: Map<string, string>)
    (initialEvents: CalendarEvent list)
    (test: InvoiceSyncApi -> unit)
    =
    let eventsState = ResizeArray<CalendarEvent>(initialEvents)
    let mutable eventCounter = eventsState.Count

    let allLedgerKeys () =
        invoicesInWindow
        |> List.map (fun invoice -> InvoiceSyncKey.derive invoice.SupplierId invoice.Reference)
        |> Set.ofList

    let buildPlanNow () =
        diff { InWindow = invoicesInWindow; AllLedgerKeys = allLedgerKeys () } (List.ofSeq eventsState)

    let createCalendarEvent: CreateCalendarEvent =
        fun _accountId _calendarId syncKey allDayEvent ->
            eventCounter <- eventCounter + 1
            let id = CalendarEventId.create $"fake-evt-{eventCounter}" |> valueOrFail
            eventsState.Add { Id = id; Event = allDayEvent; SyncKey = Some syncKey }
            Ok id

    let updateCalendarEvent: UpdateCalendarEvent =
        fun _accountId _calendarId eventId allDayEvent ->
            match eventsState |> Seq.tryFindIndex (fun e -> e.Id = eventId) with
            | Some index ->
                eventsState.[index] <- { eventsState.[index] with Event = allDayEvent }
                Ok()
            | None -> Error(EventNoLongerExists eventId)

    let deleteCalendarEvent: DeleteCalendarEvent =
        fun _accountId _calendarId eventId ->
            match eventsState |> Seq.tryFindIndex (fun e -> e.Id = eventId) with
            | Some index ->
                eventsState.RemoveAt index
                Ok()
            | None -> Error(EventNoLongerExists eventId)

    let markSynced: MarkSynced = fun _ _ _ _ -> Ok()
    let clearSyncRecord: ClearSyncRecord = fun _ -> Ok()

    let getSyncPlan () : Result<SyncViewUiType, MyDogsbodyException> =
        match readyAccount with
        | None ->
            Ok
                { StatusByInvoiceId = Map.empty
                  Plan = []
                  OrphanedEvents = []
                  NotReadyReason = Some notReadyMessage }
        | Some _ ->
            let plan = buildPlanNow ()

            Ok
                { StatusByInvoiceId = InvoiceSyncApiMappers.toSyncStatusByInvoiceId invoicesInWindow plan
                  Plan = plan |> List.choose (InvoiceSyncApiMappers.toSyncPlanRowUiType supplierNamesById)
                  OrphanedEvents = InvoiceSyncApiMappers.toOrphanedEvents (List.ofSeq eventsState) plan
                  NotReadyReason = None }

    let executeSyncPlan (selectedRows: SyncPlanRowUiType list) : Result<SyncOutcomeRowUiType list, MyDogsbodyException> =
        match readyAccount with
        | None -> Error(MyDogsbodyException("InvoiceSyncApiContractTests.fake", notReadyMessage, ApplicationException notReadyMessage))
        | Some(accId, calId) ->
            let fullPlan = buildPlanNow ()
            // Matches InvoiceSyncApiFactory.executeSyncPlan's own selection logic exactly - a
            // dependency-type/API fake must not drift from the real implementation (CLAUDE.md's
            // contract rule). Keyed by SyncKey, not bare Reference: two different suppliers can
            // share the same reference text, so Reference alone is not unique (PR #23 review round 1).
            let selectedSyncKeys = selectedRows |> List.map (fun row -> row.SyncKey) |> Set.ofList

            let syncKeyOf =
                function
                | CreateEvent invoice
                | UpdateEvent(_, invoice) -> InvoiceSyncKey.derive invoice.SupplierId invoice.Reference |> InvoiceSyncKey.value
                | DeleteEvent(_, key) -> InvoiceSyncKey.value key
                | LeaveAlone _ -> ""

            let actionsToRun =
                fullPlan
                |> List.filter (function
                    | LeaveAlone _ -> false
                    | planAction -> List.isEmpty selectedRows || Set.contains (syncKeyOf planAction) selectedSyncKeys)

            let outcomes =
                executePlan createCalendarEvent updateCalendarEvent deleteCalendarEvent markSynced clearSyncRecord accId calId actionsToRun
                |> function
                    | Ok o -> o
                    | Error _ -> []

            List.zip (actionsToRun |> List.truncate outcomes.Length) outcomes
            |> List.choose (InvoiceSyncApiMappers.toSyncOutcomeRowUiType supplierNamesById)
            |> Ok

    test { GetSyncPlan = getSyncPlan; ExecuteSyncPlan = executeSyncPlan }

let private withNotReadyFakeApi (test: InvoiceSyncApi -> unit) = withFakeApi None [] Map.empty [] test

// ---------- test data ----------

let private supplierId = SupplierId.create "1" |> valueOrFail
let private supplierNamesById = Map.ofList [ "1", "Acme" ]
let private accountId = GoogleAccountId.create "acct-1" |> valueOrFail
let private calendarId = CalendarId.create "cal-1" |> valueOrFail

let private anUploadableInvoice (id: string) (reference: string) (dueDate: DateTime) : UploadableInvoice =
    { Id = InvoiceId.create id |> valueOrFail
      SupplierId = supplierId
      Reference = InvoiceReference.create reference |> valueOrFail
      Amount = Money.create 100m "AUD" |> valueOrFail
      DueDate = InvoiceDueDate.create dueDate |> valueOrFail }

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error(caughtException: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {caughtException.Message}"

let private errorOrFail label result =
    match result with
    | Error(caughtException: MyDogsbodyException) -> caughtException
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

// ---------- the shared suite: the only paths reachable on both the real record and the fake ----------

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real api" |]; [| box "fake api" |] ]

let private withImplementation (name: string) (test: InvoiceSyncApi -> unit) =
    match name with
    | "real api" -> withRealApi test
    | "fake api" -> withNotReadyFakeApi test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``GetSyncPlan reports NotReadyReason, an empty plan and no orphans when no Google account is registered``
    (implementation: string)
    =
    withImplementation implementation (fun api ->
        let view = api.GetSyncPlan() |> okOrFail "GetSyncPlan"
        Assert.Equal(Some notReadyMessage, view.NotReadyReason)
        Assert.Empty view.Plan
        Assert.Empty view.OrphanedEvents
        Assert.Empty view.StatusByInvoiceId)

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``ExecuteSyncPlan fails with the not-ready message when no Google account is registered`` (implementation: string) =
    withImplementation implementation (fun api ->
        let actual = api.ExecuteSyncPlan [] |> errorOrFail "ExecuteSyncPlan"
        Assert.Equal(notReadyMessage, actual.Message))

// ---------- the real API only: what cannot be run against the fake ----------

[<Fact; Trait("Level", "Contract")>]
let ``the real API reports a ready-but-unauthorised account as needing re-authorisation, without reaching Google`` () =
    withRealApiReadyButUnauthorised (fun api ->
        let actual = api.GetSyncPlan() |> errorOrFail "GetSyncPlan"
        Assert.Equal("This Google account needs to be re-authorised.", actual.Message))

[<Fact; Trait("Level", "Contract")>]
let ``the real API's ExecuteSyncPlan also reports a ready-but-unauthorised account as needing re-authorisation`` () =
    withRealApiReadyButUnauthorised (fun api ->
        let actual = api.ExecuteSyncPlan [] |> errorOrFail "ExecuteSyncPlan"
        Assert.Equal("This Google account needs to be re-authorised.", actual.Message))

// ---------- the fake only: scenarios that need a successful Google read/write, which the real
// InvoiceSyncApi cannot be driven through in this suite - see this file's header comment. ----------

[<Fact; Trait("Level", "Contract")>]
let ``fake api: an outstanding plan is returned with every action naming the invoice`` () =
    let missingInvoice = anUploadableInvoice "1" "INV-100" (DateTime(2026, 9, 20))
    let changedInvoice = anUploadableInvoice "2" "INV-200" (DateTime(2026, 9, 25))

    let existingEventForChanged: CalendarEvent =
        { Id = CalendarEventId.create "evt-existing" |> valueOrFail
          Event = { Date = changedInvoice.DueDate |> InvoiceDueDate.value; Title = "Wrong title"; Description = "" }
          SyncKey = Some(InvoiceSyncKey.derive changedInvoice.SupplierId changedInvoice.Reference) }

    withFakeApi
        (Some(accountId, calendarId))
        [ missingInvoice; changedInvoice ]
        supplierNamesById
        [ existingEventForChanged ]
        (fun api ->
            let view = api.GetSyncPlan() |> okOrFail "GetSyncPlan"
            Assert.Equal(None, view.NotReadyReason)
            Assert.Equal(2, view.Plan.Length)

            let missingRow = view.Plan |> List.find (fun row -> row.Reference = "INV-100")
            Assert.Equal(CreateSyncAction, missingRow.Action)
            Assert.Equal(Some(InvoiceId.value missingInvoice.Id), missingRow.InvoiceId)
            Assert.Equal("Acme", missingRow.SupplierName)

            let changedRow = view.Plan |> List.find (fun row -> row.Reference = "INV-200")
            Assert.Equal(UpdateSyncAction, changedRow.Action)
            Assert.Equal(Some(InvoiceId.value changedInvoice.Id), changedRow.InvoiceId)
            Assert.Equal("Acme", changedRow.SupplierName))

[<Fact; Trait("Level", "Contract")>]
let ``fake api: ExecuteSyncPlan with an empty list runs everything outstanding`` () =
    let firstInvoice = anUploadableInvoice "1" "INV-100" (DateTime(2026, 9, 20))
    let secondInvoice = anUploadableInvoice "2" "INV-200" (DateTime(2026, 9, 25))

    withFakeApi (Some(accountId, calendarId)) [ firstInvoice; secondInvoice ] supplierNamesById [] (fun api ->
        let outcomes = api.ExecuteSyncPlan [] |> okOrFail "ExecuteSyncPlan"

        Assert.Equal(2, outcomes.Length)
        Assert.Contains(outcomes, fun row -> row.Reference = "INV-100" && row.Result = SyncSucceeded)
        Assert.Contains(outcomes, fun row -> row.Reference = "INV-200" && row.Result = SyncSucceeded))

[<Fact; Trait("Level", "Contract")>]
let ``fake api: ExecuteSyncPlan with a non-empty list runs only the rows matching by SyncKey`` () =
    let firstInvoice = anUploadableInvoice "1" "INV-100" (DateTime(2026, 9, 20))
    let secondInvoice = anUploadableInvoice "2" "INV-200" (DateTime(2026, 9, 25))

    withFakeApi (Some(accountId, calendarId)) [ firstInvoice; secondInvoice ] supplierNamesById [] (fun api ->
        let selectedRow: SyncPlanRowUiType =
            { InvoiceId = Some(InvoiceId.value firstInvoice.Id)
              SupplierName = "Acme"
              Reference = "INV-100"
              SyncKey = InvoiceSyncKey.derive firstInvoice.SupplierId firstInvoice.Reference |> InvoiceSyncKey.value
              DueDate = Some(InvoiceDueDate.value firstInvoice.DueDate)
              Action = CreateSyncAction }

        let outcomes = api.ExecuteSyncPlan [ selectedRow ] |> okOrFail "ExecuteSyncPlan"

        let outcome = Assert.Single outcomes
        Assert.Equal("INV-100", outcome.Reference)
        Assert.Equal(SyncSucceeded, outcome.Result))

[<Fact; Trait("Level", "Contract")>]
let ``fake api: ExecuteSyncPlan does not act on a different supplier's invoice sharing the same reference text`` () =
    // Two different suppliers, same invoice reference text ("INV-100") - a realistic collision:
    // the ledger's unique index is on (supplier, reference), not on reference alone, so nothing
    // stops two different suppliers from using the same invoice numbering. Regression test for
    // PR #23 review round 1: selecting by bare Reference let a tick on one supplier's row also
    // fire a different supplier's action for the same reference text.
    let otherSupplierId = SupplierId.create "2" |> valueOrFail
    let namesById = Map.ofList [ "1", "Acme"; "2", "Widgets Inc" ]

    let tickedInvoice = anUploadableInvoice "1" "INV-100" (DateTime(2026, 9, 20))

    let otherSuppliersInvoice: UploadableInvoice =
        { Id = InvoiceId.create "2" |> valueOrFail
          SupplierId = otherSupplierId
          Reference = InvoiceReference.create "INV-100" |> valueOrFail
          Amount = Money.create 250m "AUD" |> valueOrFail
          DueDate = InvoiceDueDate.create (DateTime(2026, 9, 25)) |> valueOrFail }

    withFakeApi (Some(accountId, calendarId)) [ tickedInvoice; otherSuppliersInvoice ] namesById [] (fun api ->
        // The user ticks only tickedInvoice's row (Supplier "Acme") - never the other supplier's.
        let selectedRow: SyncPlanRowUiType =
            { InvoiceId = Some(InvoiceId.value tickedInvoice.Id)
              SupplierName = "Acme"
              Reference = "INV-100"
              SyncKey = InvoiceSyncKey.derive tickedInvoice.SupplierId tickedInvoice.Reference |> InvoiceSyncKey.value
              DueDate = Some(InvoiceDueDate.value tickedInvoice.DueDate)
              Action = CreateSyncAction }

        let outcomes = api.ExecuteSyncPlan [ selectedRow ] |> okOrFail "ExecuteSyncPlan"

        // Only the ticked invoice should have been acted on - the other supplier's same-numbered
        // invoice was never selected and must be left alone.
        Assert.Equal(1, outcomes.Length)

        // Which one actually ran, not just how many: re-derive the plan and confirm the ticked
        // invoice is now up to date (its create ran) while the untouched supplier's is still
        // reported missing (its create did NOT run).
        let viewAfter = api.GetSyncPlan() |> okOrFail "GetSyncPlan"
        Assert.Equal(Some UpToDateSync, viewAfter.StatusByInvoiceId |> Map.tryFind (InvoiceId.value tickedInvoice.Id))

        Assert.Equal(
            Some MissingSync,
            viewAfter.StatusByInvoiceId |> Map.tryFind (InvoiceId.value otherSuppliersInvoice.Id)
        ))

[<Fact; Trait("Level", "Contract")>]
let ``fake api: ExecuteSyncPlan does not delete an orphaned event whose reference text collides with a ticked invoice``
    ()
    =
    // The severe form of the same collision (PR #23 review round 1): an outstanding DELETE (its
    // invoice already gone from the ledger) has no InvoiceId of its own, so
    // InvoicesComponents.rowsPendingSync / InvoicesModuleCreators.rowsMatchingSelection both
    // exclude every delete row the moment ANY invoice is ticked (they filter on InvoiceId, and a
    // delete's is always None). That means the page's Q2.13 confirmation dialog - which only ever
    // inspects rowsPendingSync's result - could never even see this delete to ask about it. Before
    // the fix, ExecuteSyncPlan's own Reference-based matching would still have run it anyway if its
    // reference text collided with the ticked invoice: a delete executed with NO confirmation ever
    // shown, on an invoice the user never selected.
    let tickedInvoice = anUploadableInvoice "1" "INV-100" (DateTime(2026, 9, 20))
    let namesById = Map.ofList [ "1", "Acme"; "2", "Widgets Inc" ]

    let otherSupplierId = SupplierId.create "2" |> valueOrFail
    let otherSupplierReference = InvoiceReference.create "INV-100" |> valueOrFail
    let orphanedEventKey = InvoiceSyncKey.derive otherSupplierId otherSupplierReference

    let orphanedEvent: CalendarEvent =
        { Id = CalendarEventId.create "evt-orphaned" |> valueOrFail
          Event = { Date = DateTime(2026, 8, 1); Title = "Invoice due: INV-100"; Description = "" }
          SyncKey = Some orphanedEventKey }

    // tickedInvoice is the only invoice in the ledger/window - the other supplier's invoice has
    // already left the ledger entirely, which is what makes its event's action a DeleteEvent.
    withFakeApi (Some(accountId, calendarId)) [ tickedInvoice ] namesById [ orphanedEvent ] (fun api ->
        let selectedRow: SyncPlanRowUiType =
            { InvoiceId = Some(InvoiceId.value tickedInvoice.Id)
              SupplierName = "Acme"
              Reference = "INV-100"
              SyncKey = InvoiceSyncKey.derive tickedInvoice.SupplierId tickedInvoice.Reference |> InvoiceSyncKey.value
              DueDate = Some(InvoiceDueDate.value tickedInvoice.DueDate)
              Action = CreateSyncAction }

        let outcomes = api.ExecuteSyncPlan [ selectedRow ] |> okOrFail "ExecuteSyncPlan"

        // Only the ticked create ran.
        let outcome = Assert.Single outcomes
        Assert.Equal(CreateSyncAction, outcome.Action)

        // The orphaned event must still be outstanding as a delete - never executed, and never
        // even offered for confirmation, since nobody selected it.
        let viewAfter = api.GetSyncPlan() |> okOrFail "GetSyncPlan"

        Assert.Contains(
            viewAfter.Plan,
            fun row -> row.Action = DeleteSyncAction && row.Reference = "INV-100" && row.SupplierName = "Widgets Inc"
        ))
