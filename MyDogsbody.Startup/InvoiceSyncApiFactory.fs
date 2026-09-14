/// Where the abstract meets the real for calendar sync: the adapters that satisfy the events-half
/// dependency function types, DiffInvoicesAgainstCalendarWorkflow.diff and
/// SyncInvoicesToCalendarWorkflow.executePlan partially applied over them, and the two error
/// types' translation.
///
/// The only place that knows the main SQLite database, the Google integration AND the domain for
/// this area. Dependencies are leading parameters; no module-level bindings.
///
/// Does not call DiffInvoicesAgainstCalendarWorkflow.buildPlan: GetSyncPlan needs the intermediate
/// `inWindow` value (to classify every invoice as up to date/missing/changed - design decision 5)
/// that buildPlan does not expose, and calling `listCalendarEvents` a second time to get it would
/// mean two Google reads that could disagree with each other. Both members instead compose the
/// same pieces buildPlan does directly, in the same order buildPlan uses - the calendar read
/// bound in this `result` pipeline BEFORE `diff` is called, exactly as hazard (b) requires - and
/// call `diff` itself once, so the hazard guarantees anchor to the same tested function either way.
module MyDogsbody.Startup.InvoiceSyncApiFactory

open System
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Domain.Calendar.DiffInvoicesAgainstCalendarWorkflow
open MyDogsbody.Domain.Calendar.SyncInvoicesToCalendarWorkflow
open MyDogsbody.Database
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database.Types
open MyDogsbody.UI.Types

let createInvoiceSyncApi
    (handleError: HandleErrorBuilder)
    (getCurrentTime: unit -> DateTime)
    (databaseContext: DatabaseContext)
    (googleContext: GoogleDatabaseContext)
    : InvoiceSyncApi =

    let databaseConnection = databaseContext.GetDatabaseConnection
    let toInvoiceError = InvoiceApiMappers.toInvoiceError
    let toException = GoogleAccountApiMappers.toMyDogsbodyException

    // ---------- main-database adapters ----------

    let loadInvoices: LoadInvoices =
        fun cutoff ->
            InvoiceStore.getInvoices handleError databaseConnection databaseContext.GetInvoices cutoff
            |> Result.mapError toInvoiceError

    let loadSuppliers: LoadSuppliers =
        fun () ->
            SupplierStore.getAll handleError databaseConnection databaseContext.GetSuppliers databaseContext.GetSupplierMatchers ()
            |> Result.mapError (fun caughtException -> SupplierStoreFailed caughtException.Message)

    /// A supplierId -> name map for the top mapper, the same idiom InvoiceApiFactory uses.
    let supplierNames (action: string) : Result<Map<string, string>, MyDogsbodyException> =
        loadSuppliers ()
        |> Result.map (fun suppliers ->
            suppliers
            |> List.map (fun supplier -> SupplierId.value supplier.Id, SupplierName.value supplier.Name)
            |> Map.ofList)
        |> Result.mapError (SupplierApiMappers.toMyDogsbodyException action)

    let loadAllLedgerKeys: LoadAllLedgerKeys =
        fun () ->
            InvoiceCalendarEventStore.loadAllLedgerKeys handleError databaseConnection databaseContext.GetInvoices ()
            |> Result.mapError toInvoiceError

    let markSynced: MarkSynced =
        fun invoiceId accountId calendarId eventId ->
            InvoiceCalendarEventStore.markSynced handleError databaseConnection getCurrentTime invoiceId accountId calendarId eventId
            |> Result.mapError toInvoiceError

    let clearSyncRecord: ClearSyncRecord =
        fun eventId ->
            InvoiceCalendarEventStore.clearSyncRecord handleError databaseConnection eventId |> Result.mapError toInvoiceError

    /// The current window, resolved the same way ScanWindowApiFactory.GetSelectedScanWindow is -
    /// the remembered choice, or the fallback if it no longer exists.
    let resolvedWindow () : Result<ScanWindowDays, MyDogsbodyException> =
        result {
            let! windows = ScanWindowStore.getScanWindows handleError databaseConnection databaseContext.GetScanWindows ()
            let! remembered = ScanWindowStore.getSelectedScanWindow handleError databaseConnection ()
            return ResolveScanWindowWorkflow.resolveScanWindow windows remembered
        }

    // ---------- Google adapters ----------

    let listGoogleAccounts: ListGoogleAccounts =
        GoogleAccountApiFactory.bindListGoogleAccounts handleError googleContext

    let listCalendarEvents: ListCalendarEvents =
        GoogleAccountApiFactory.bindListCalendarEvents handleError googleContext GoogleCalendarClient.listEvents

    let createCalendarEvent: CreateCalendarEvent =
        GoogleAccountApiFactory.bindCreateCalendarEvent handleError googleContext GoogleCalendarClient.createEvent

    let updateCalendarEvent: UpdateCalendarEvent =
        GoogleAccountApiFactory.bindUpdateCalendarEvent handleError googleContext GoogleCalendarClient.updateEvent

    let deleteCalendarEvent: DeleteCalendarEvent =
        GoogleAccountApiFactory.bindDeleteCalendarEvent handleError googleContext GoogleCalendarClient.deleteEvent

    /// The account this change syncs to: the first registered account with a default invoice
    /// calendar chosen (Q2.11's READY state). Syncing to more than one ready account at once is
    /// out of scope for this change - design.md's examples work in terms of "the account"
    /// throughout, and nothing in the domain associates an invoice with a particular Google
    /// account. Recorded as a known limitation, not silently decided.
    let readyAccount (action: string) : Result<RegisteredGoogleAccount option, MyDogsbodyException> =
        ListGoogleAccountsWorkflow.listGoogleAccounts listGoogleAccounts ()
        |> Result.map (List.tryFind (fun account -> Option.isSome account.DefaultInvoiceCalendar))
        |> Result.mapError (toException action)

    let notReadyMessage =
        "No Google account is ready to sync to yet. Register one on the Google accounts page and choose its default invoice calendar."

    /// The shared middle of both members: the ready account's calendar, the plan (diff'd once,
    /// against one calendar read), and every invoice in the current window. `None` when no
    /// account is ready - the caller decides what that means for its own return shape.
    let planAndContext
        (action: string)
        : Result<(RegisteredGoogleAccount * CalendarId * UploadableInvoice list * SyncAction list * CalendarEvent list) option, MyDogsbodyException> =
        result {
            let! accountOption = readyAccount action

            match accountOption with
            | None -> return None
            | Some account ->
                let calendarId = Option.get account.DefaultInvoiceCalendar
                let! window = resolvedWindow ()
                let cutoff = Some(ScanForInvoicesWorkflow.computeCutoff getCurrentTime window)

                let! storedInWindow = loadInvoices cutoff |> Result.mapError (InvoiceApiMappers.toMyDogsbodyException action)
                let inWindow = storedInWindow |> List.choose UploadableInvoice.ofStored

                let! allKeys = loadAllLedgerKeys () |> Result.mapError (InvoiceApiMappers.toMyDogsbodyException action)

                let range = CalendarDateRangeWorkflow.derive getCurrentTime window inWindow

                // THE BIND THAT MATTERS, same as buildPlan: the calendar read happens here, before
                // `diff` is called - a failed read short-circuits this whole `result` block and
                // `diff` is never reached. Friction #18 (b).
                let! events = listCalendarEvents account.Id calendarId range |> Result.mapError (toException action)

                let plan = diff { InWindow = inWindow; AllLedgerKeys = allKeys } events

                return Some(account, calendarId, inWindow, plan, events)
        }

    let getSyncPlan () : Result<SyncViewUiType, MyDogsbodyException> =
        let action = ActionNames.MyDogsbody.Startup.InvoiceSyncApi.getSyncPlan

        result {
            let! names = supplierNames action
            let! context = planAndContext action

            return
                match context with
                | None ->
                    { StatusByInvoiceId = Map.empty
                      Plan = []
                      OrphanedEvents = []
                      NotReadyReason = Some notReadyMessage }
                | Some(_, _, inWindow, plan, events) ->
                    { StatusByInvoiceId = InvoiceSyncApiMappers.toSyncStatusByInvoiceId inWindow plan
                      Plan = plan |> List.choose (InvoiceSyncApiMappers.toSyncPlanRowUiType names)
                      OrphanedEvents = InvoiceSyncApiMappers.toOrphanedEvents events plan
                      NotReadyReason = None }
        }

    let executeSyncPlan (selectedRows: SyncPlanRowUiType list) : Result<SyncOutcomeRowUiType list, MyDogsbodyException> =
        let action = ActionNames.MyDogsbody.Startup.InvoiceSyncApi.executeSyncPlan

        result {
            let! names = supplierNames action
            let! context = planAndContext action

            match context with
            | None -> return! Error(MyDogsbodyException(action, notReadyMessage, ApplicationException notReadyMessage))
            | Some(account, calendarId, _, fullPlan, _) ->
                // Q2.7: the selection when there is one, everything outstanding when there is not.
                // Selected by Reference - the one field every plan-row kind carries - rather than
                // by re-trusting an event id or invoice id the preview handed back, so a plan that
                // has moved on since the preview (a rescan, a due-date change) still selects the
                // right CURRENT action for that invoice rather than a stale one.
                let selectedReferences = selectedRows |> List.map (fun row -> row.Reference) |> Set.ofList

                let referenceOf =
                    function
                    | CreateEvent invoice
                    | UpdateEvent(_, invoice) -> InvoiceReference.value invoice.Reference
                    | DeleteEvent(_, key) -> InvoiceSyncKey.parts key |> Option.map snd |> Option.defaultValue ""
                    | LeaveAlone _ -> ""

                let actionsToRun =
                    fullPlan
                    |> List.filter (function
                        | LeaveAlone _ -> false
                        | planAction -> List.isEmpty selectedRows || Set.contains (referenceOf planAction) selectedReferences)

                let! outcomes =
                    executePlan
                        createCalendarEvent
                        updateCalendarEvent
                        deleteCalendarEvent
                        markSynced
                        clearSyncRecord
                        account.Id
                        calendarId
                        actionsToRun
                    |> Result.mapError (toException action)

                // executePlan may stop early (CalendarNoLongerExists/NotAuthorised), so outcomes
                // can be shorter than actionsToRun - truncate the longer list to zip, never the
                // reverse.
                return
                    List.zip (actionsToRun |> List.truncate outcomes.Length) outcomes
                    |> List.choose (InvoiceSyncApiMappers.toSyncOutcomeRowUiType names)
        }

    { GetSyncPlan = getSyncPlan
      ExecuteSyncPlan = executeSyncPlan }
