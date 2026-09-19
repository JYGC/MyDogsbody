module MyDogsbody.Tests.UI.ModuleCreators.InvoicesModuleCreatorsTests

open System
open Xunit
open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types

let private runSynchronously (work: unit -> unit) = work ()

let private failure message =
    MyDogsbodyException("test.action", message, ApplicationException(message))

let private anInvoice reference : InvoiceUiType =
    { Id = reference
      SupplierName = "Acme"
      Reference = reference
      Amount = 10m
      Currency = "AUD"
      IssueDate = None
      DueDate = None
      MessageReceivedAt = DateTime(2026, 5, 1)
      CanBecomeCalendarEvent = false
      CannotUploadReason = Some "no due date"
      SyncStatus = None }

let private aWindow days : ScanWindowUiType =
    { Id = string days; Days = days; Label = $"mail received in the last {days} days" }

type private ScanWindowApiSpy() =
    member val SelectedCalls: int list = [] with get, set
    member val GetSelectedResult: Result<int, MyDogsbodyException> = Ok 14 with get, set

    member val GetScanWindowsResult: Result<ScanWindowUiType list, MyDogsbodyException> =
        Ok [ aWindow 7; aWindow 14; aWindow 90 ] with get, set

    member this.Api: ScanWindowApi =
        { GetScanWindows = fun () -> this.GetScanWindowsResult
          AddScanWindow = fun _ -> Ok()
          DeleteScanWindow = fun _ -> Ok()
          GetSelectedScanWindow = fun () -> this.GetSelectedResult
          SelectScanWindow =
            fun days ->
                this.SelectedCalls <- this.SelectedCalls @ [ days ]
                Ok() }

let private aSyncPlanRow (invoiceId: string option) (reference: string) (action: SyncPlanActionUiType) : SyncPlanRowUiType =
    { InvoiceId = invoiceId
      SupplierName = "Acme"
      Reference = reference
      // Not exercised by these module-creator tests, which select by InvoiceId - a distinct
      // placeholder is enough to keep this record shape valid.
      SyncKey = $"sync-key-{reference}"
      DueDate = None
      Action = action }

type private InvoiceSyncApiSpy() =
    member val GetSyncPlanCallCount: int = 0 with get, set

    member val GetSyncPlanResult: Result<SyncViewUiType, MyDogsbodyException> =
        Ok
            { StatusByInvoiceId = Map.empty
              Plan = []
              OrphanedEvents = []
              NotReadyReason = None } with get, set

    member val ExecuteSyncPlanCalls: SyncPlanRowUiType list list = [] with get, set
    member val ExecuteSyncPlanResult: Result<SyncOutcomeRowUiType list, MyDogsbodyException> = Ok [] with get, set

    member this.Api: InvoiceSyncApi =
        { GetSyncPlan =
            fun () ->
                this.GetSyncPlanCallCount <- this.GetSyncPlanCallCount + 1
                this.GetSyncPlanResult
          ExecuteSyncPlan =
            fun rows ->
                this.ExecuteSyncPlanCalls <- this.ExecuteSyncPlanCalls @ [ rows ]
                this.ExecuteSyncPlanResult }

type private InvoiceApiSpy() =
    member val ScanCalls: int list = [] with get, set
    member val RescanEverythingCalls: int list = [] with get, set
    member val GetInvoicesCalls: int list = [] with get, set
    member val GetProblemsCalls: int = 0 with get, set
    member val ScanResult: Result<ScanResultUiType, MyDogsbodyException> = Ok { Invoices = []; Problems = [] } with get, set
    /// Per-window ledger contents, so a test can prove narrowing re-queries for the smaller window.
    member val GetInvoicesFor: int -> InvoiceUiType list = (fun _ -> []) with get, set
    /// The persisted problem rows GetProblems returns - Q1.19 keeps them across scans.
    member val ProblemsInStore: ScanProblemUiType list = [] with get, set
    /// Set to make the LEDGER read fail while Scan still succeeds - the store can refuse a read
    /// (an unusable stored row, a locked file) without the mailbox read having failed.
    member val GetInvoicesError: MyDogsbodyException option = None with get, set
    member val GetProblemsError: MyDogsbodyException option = None with get, set

    member this.Api: InvoiceApi =
        { Scan =
            fun days ->
                this.ScanCalls <- this.ScanCalls @ [ days ]
                this.ScanResult
          RescanEverything =
            fun days ->
                this.RescanEverythingCalls <- this.RescanEverythingCalls @ [ days ]
                this.ScanResult
          GetInvoices =
            fun days ->
                this.GetInvoicesCalls <- this.GetInvoicesCalls @ [ days ]

                match this.GetInvoicesError with
                | Some readException -> Error readException
                | None -> Ok(this.GetInvoicesFor days)
          DeleteInvoice = fun _ -> Ok()
          GetProblems =
            fun () ->
                this.GetProblemsCalls <- this.GetProblemsCalls + 1

                match this.GetProblemsError with
                | Some readException -> Error readException
                | None -> Ok this.ProblemsInStore
          GetTombstones = fun () -> Ok []
          UndeleteInvoice = fun _ _ -> Ok() }

[<Fact; Trait("Level", "Unit")>]
let ``the initial window comes from the API's resolved choice, never a literal 14`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 90)
    let invoiceApi = InvoiceApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    Assert.Equal(90, AVal.force invoicesModule.SelectedWindowDaysAval)
    // it scanned exactly the resolved window
    Assert.Equal<int list>([ 90 ], invoiceApi.ScanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``selecting a window persists the choice and reloads the ledger WITHOUT scanning the mailbox`` () =
    // Q1.9 fallback (12.4 measured a full re-read at ~60 s): a window change filters the ledger
    // that is already stored, it does not read the mailbox again. A scan is invoicesModule.Rescan only.
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    invoicesModule.SelectWindow 7

    Assert.Equal<int list>([ 7 ], windowApi.SelectedCalls)
    Assert.Equal(7, AVal.force invoicesModule.SelectedWindowDaysAval)
    // the ledger was re-queried for the new window...
    Assert.Contains(7, invoiceApi.GetInvoicesCalls)
    // ...and the mailbox was NOT scanned: only the initial load's scan (14) ever ran
    Assert.Equal<int list>([ 14 ], invoiceApi.ScanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``narrowing the window re-queries the ledger for the smaller window`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 90)

    let invoiceApi =
        InvoiceApiSpy(GetInvoicesFor = (fun days -> if days >= 90 then [ anInvoice "OLD"; anInvoice "NEW" ] else [ anInvoice "NEW" ]))

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    invoicesModule.SelectWindow 7
    Assert.Equal<string list>([ "NEW" ], AVal.force invoicesModule.InvoicesAval |> List.map (fun invoice -> invoice.Reference))

    // widening brings the out-of-window invoice back - it was hidden, not forgotten
    invoicesModule.SelectWindow 90
    Assert.Equal<string list>([ "OLD"; "NEW" ], AVal.force invoicesModule.InvoicesAval |> List.map (fun invoice -> invoice.Reference))

[<Fact; Trait("Level", "Unit")>]
let ``Rescan reads the mailbox for the current window`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    invoicesModule.Rescan()

    // the initial load's scan (30) then the explicit Rescan (30)
    Assert.Equal<int list>([ 30; 30 ], invoiceApi.ScanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``Rescan everything reads the mailbox via RescanEverything for the current window, not Scan`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy(GetInvoicesFor = (fun _ -> [ anInvoice "INV-1" ]))

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    invoicesModule.RescanEverything()

    // the watermark-clearing scan ran for the current window...
    Assert.Equal<int list>([ 30 ], invoiceApi.RescanEverythingCalls)
    // ...and the ordinary Scan ran only on the initial load
    Assert.Equal<int list>([ 30 ], invoiceApi.ScanCalls)
    // the table is read back from the store afterward, same as Rescan
    Assert.Equal<string list>([ "INV-1" ], AVal.force invoicesModule.InvoicesAval |> List.map (fun invoice -> invoice.Reference))

let private aProblem messageId : ScanProblemUiType =
    { SourceMessageId = messageId
      Sender = "billing@acme.test"
      Subject = "Statement"
      ReceivedAt = DateTime(2026, 5, 1)
      Cause = "No supplier's matchers recognised this message."
      RecordedAt = DateTime(2026, 5, 2) }

[<Fact; Trait("Level", "Unit")>]
let ``a scan that finds no new mail leaves the stored ledger on screen`` () =
    // ScanResult carries only what THIS scan did (design.md -> design deviation 6: "the page's
    // full view comes from GetInvoices / GetProblems"). Watermarks mean the second and every
    // later scan of an unchanged mailbox reads no messages and so returns an EMPTY list - and
    // the initial page load scans, so a returning user whose watermarks are current would open
    // on a blank table while the ledger is still in the store.
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy(GetInvoicesFor = (fun _ -> [ anInvoice "INV-STORED" ]))
    // ScanResult stays the default Ok { Invoices = []; Problems = [] } - "nothing new in the mail"

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    Assert.Equal<string list>(
        [ "INV-STORED" ],
        AVal.force invoicesModule.InvoicesAval |> List.map (fun invoice -> invoice.Reference)
    )

    invoicesModule.Rescan()

    Assert.Equal<string list>(
        [ "INV-STORED" ],
        AVal.force invoicesModule.InvoicesAval |> List.map (fun invoice -> invoice.Reference)
    )

[<Fact; Trait("Level", "Unit")>]
let ``a scan that records no new problems leaves the persisted problem rows on screen`` () =
    // Q1.19: problem rows are PERSISTED "so incremental scanning does not empty the diagnostic
    // list before it is looked at" (InvoicesTypes.ScanProblemCause). Taking the problems view
    // from the scan result undoes exactly that.
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy(ProblemsInStore = [ aProblem "m1" ])

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    Assert.Equal<string list>(
        [ "m1" ],
        AVal.force invoicesModule.ProblemsAval |> List.map (fun problem -> problem.SourceMessageId)
    )

    invoicesModule.Rescan()

    Assert.Equal<string list>(
        [ "m1" ],
        AVal.force invoicesModule.ProblemsAval |> List.map (fun problem -> problem.SourceMessageId)
    )

[<Fact; Trait("Level", "Unit")>]
let ``a scan failure sets ErrorAval; a later success clears it`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy(ScanResult = Error(failure "the store is unreachable"))

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    Assert.Equal(Some "the store is unreachable", AVal.force invoicesModule.ErrorAval)

    // The scan now succeeds and stores INV-1. The table is read back from the STORE, not from
    // ScanResult (design decision 6), so that is where the spy holds it.
    invoiceApi.ScanResult <- Ok { Invoices = [ anInvoice "INV-1" ]; Problems = [] }
    invoiceApi.GetInvoicesFor <- fun _ -> [ anInvoice "INV-1" ]
    invoicesModule.Rescan()

    Assert.Equal(None, AVal.force invoicesModule.ErrorAval)

    Assert.Equal<string list>(
        [ "INV-1" ],
        AVal.force invoicesModule.InvoicesAval |> List.map (fun invoice -> invoice.Reference)
    )

[<Fact; Trait("Level", "Unit")>]
let ``deleting an invoice reloads the current window WITHOUT scanning the mailbox`` () =
    // The row is hard-deleted, so GetInvoices no longer returns it - a full scan (~60 s on a wide
    // window) to confirm that would be the exact surprise the Q1.9 fallback removes.
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    invoicesModule.DeleteInvoice "INV-9"

    // the ledger was re-queried for the current window...
    Assert.Equal<int list>([ 30; 30 ], invoiceApi.GetInvoicesCalls) // initial load + after delete
    // ...and only the initial load ever scanned the mailbox
    Assert.Equal<int list>([ 30 ], invoiceApi.ScanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``un-deleting an invoice scans the mailbox, since only a scan can restore the removed row`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    invoicesModule.UndeleteInvoice "1" "INV-9"

    // initial load (30) then the rescan a restore needs (30)
    Assert.Equal<int list>([ 30; 30 ], invoiceApi.ScanCalls)

// ---- a read that fails inside a composite load must not be swallowed ----
//
// `scan` and `loadLedger` each perform three or four reads and used to match every one of them
// independently, discarding all but one error. So a LEDGER read that failed while the scan
// succeeded set no alert at all - and since the initial page load scans, that is an empty table
// under a "0 invoice(s)" count line with nothing on screen to say the read failed.

[<Fact; Trait("Level", "Unit")>]
let ``a ledger read that fails during a scan is reported, not swallowed`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)

    let invoiceApi =
        InvoiceApiSpy(GetInvoicesError = Some(failure "a stored invoice is unusable"))

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    // the scan itself succeeded, so nothing else says the table is wrong
    Assert.Equal<int list>([ 30 ], invoiceApi.ScanCalls)
    Assert.Equal<InvoiceUiType list>([], AVal.force invoicesModule.InvoicesAval)
    Assert.Equal(Some "a stored invoice is unusable", AVal.force invoicesModule.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``the scan's own failure still wins over a later read's`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)

    let invoiceApi =
        InvoiceApiSpy(
            ScanResult = Error(failure "the mailbox is unreachable"),
            GetInvoicesError = Some(failure "a stored invoice is unusable")
        )

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    Assert.Equal(Some "the mailbox is unreachable", AVal.force invoicesModule.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``a problems read that fails during a window change is reported, not swallowed`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    Assert.Equal(None, AVal.force invoicesModule.ErrorAval)

    invoiceApi.GetProblemsError <- Some(failure "the problems table is unreachable")
    invoicesModule.SelectWindow 7

    // a window change reloads the ledger only (no scan), so nothing else reports this
    Assert.Equal<int list>([ 30 ], invoiceApi.ScanCalls)
    Assert.Equal(Some "the problems table is unreachable", AVal.force invoicesModule.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``a scan-window read that fails during a load is reported, not swallowed`` () =
    let windowApi = ScanWindowApiSpy(GetScanWindowsResult = Error(failure "the window list is unreachable"))
    let invoiceApi = InvoiceApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api (InvoiceSyncApiSpy()).Api

    Assert.Equal<ScanWindowUiType list>([], AVal.force invoicesModule.ScanWindowsAval)
    Assert.Equal(Some "the window list is unreachable", AVal.force invoicesModule.ErrorAval)

// ---- change #7: calendar sync (task 8.2) ----

[<Fact; Trait("Level", "Unit")>]
let ``the selection starts empty, and ToggleInvoice/ClearSelection change it without calling any API`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()
    let syncApi = InvoiceSyncApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    Assert.Empty(AVal.force invoicesModule.SelectedInvoiceIdsAval)

    invoicesModule.ToggleInvoice "INV-1"
    Assert.Equal<Set<string>>(Set.ofList [ "INV-1" ], AVal.force invoicesModule.SelectedInvoiceIdsAval)

    // ticking the same id again un-ticks it
    invoicesModule.ToggleInvoice "INV-1"
    Assert.Empty(AVal.force invoicesModule.SelectedInvoiceIdsAval)

    invoicesModule.ToggleInvoice "INV-1"
    invoicesModule.ToggleInvoice "INV-2"
    invoicesModule.ClearSelection()
    Assert.Empty(AVal.force invoicesModule.SelectedInvoiceIdsAval)

    // selection is view state only (task 8.2) - never persisted, so none of this called ExecuteSyncPlan
    Assert.Empty(syncApi.ExecuteSyncPlanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``ticking a row limits both the pending count and ExecuteSync to the ticked rows`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()

    let syncApi =
        InvoiceSyncApiSpy(
            GetSyncPlanResult =
                Ok
                    { StatusByInvoiceId = Map.empty
                      Plan =
                        [ aSyncPlanRow (Some "INV-1") "INV-1" CreateSyncAction
                          aSyncPlanRow (Some "INV-2") "INV-2" CreateSyncAction ]
                      OrphanedEvents = []
                      NotReadyReason = None }
        )

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    invoicesModule.ToggleInvoice "INV-1"
    Assert.Equal(1, AVal.force invoicesModule.PendingActionCountAval)

    invoicesModule.ExecuteSync()

    Assert.Equal<string list list>(
        [ [ "INV-1" ] ],
        syncApi.ExecuteSyncPlanCalls |> List.map (List.map (fun row -> row.Reference))
    )

[<Fact; Trait("Level", "Unit")>]
let ``no ticks shows everything outstanding, and ExecuteSync passes [] so the API re-derives the plan itself`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()

    let syncApi =
        InvoiceSyncApiSpy(
            GetSyncPlanResult =
                Ok
                    { StatusByInvoiceId = Map.empty
                      Plan =
                        [ aSyncPlanRow (Some "INV-1") "INV-1" CreateSyncAction
                          aSyncPlanRow None "INV-2" DeleteSyncAction ]
                      OrphanedEvents = []
                      NotReadyReason = None }
        )

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    // nothing ticked: the count is everything outstanding, including the delete
    Assert.Equal(2, AVal.force invoicesModule.PendingActionCountAval)

    invoicesModule.ExecuteSync()

    // Q2.7: an empty list, not the locally-held plan - a stale preview must not be trusted over the
    // API's own fresh re-derivation
    Assert.Equal<SyncPlanRowUiType list list>([ [] ], syncApi.ExecuteSyncPlanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``Rescan clears the selection - a tick against a row that may no longer exist is worse than no tick at all`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()
    let syncApi = InvoiceSyncApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    invoicesModule.ToggleInvoice "INV-1"
    Assert.NotEmpty(AVal.force invoicesModule.SelectedInvoiceIdsAval)

    invoicesModule.Rescan()

    Assert.Empty(AVal.force invoicesModule.SelectedInvoiceIdsAval)

[<Fact; Trait("Level", "Unit")>]
let ``RescanEverything clears the selection`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()
    let syncApi = InvoiceSyncApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    invoicesModule.ToggleInvoice "INV-1"
    invoicesModule.RescanEverything()

    Assert.Empty(AVal.force invoicesModule.SelectedInvoiceIdsAval)

[<Fact; Trait("Level", "Unit")>]
let ``SelectWindow clears the selection`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()
    let syncApi = InvoiceSyncApiSpy()

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    invoicesModule.ToggleInvoice "INV-1"
    invoicesModule.SelectWindow 7

    Assert.Empty(AVal.force invoicesModule.SelectedInvoiceIdsAval)

[<Fact; Trait("Level", "Unit")>]
let ``ExecuteSync records the outcomes, clears the selection on success, and reloads the plan`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()

    let syncApi =
        InvoiceSyncApiSpy(
            GetSyncPlanResult =
                Ok
                    { StatusByInvoiceId = Map.empty
                      Plan = [ aSyncPlanRow (Some "INV-1") "INV-1" CreateSyncAction ]
                      OrphanedEvents = []
                      NotReadyReason = None },
            ExecuteSyncPlanResult =
                Ok [ { SupplierName = "Acme Corp"; Reference = "INV-1"; Action = CreateSyncAction; Result = SyncSucceeded } ]
        )

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    let syncPlanReadsBeforeExecuting = syncApi.GetSyncPlanCallCount

    invoicesModule.ToggleInvoice "INV-1"
    invoicesModule.ExecuteSync()

    Assert.Equal<string list>(
        [ "INV-1" ],
        AVal.force invoicesModule.LastSyncOutcomesAval |> List.map (fun outcome -> outcome.Reference)
    )

    Assert.Empty(AVal.force invoicesModule.SelectedInvoiceIdsAval)
    // a write reloads: the plan was re-read after the execute, not just on the initial load
    Assert.True(syncApi.GetSyncPlanCallCount > syncPlanReadsBeforeExecuting)

[<Fact; Trait("Level", "Unit")>]
let ``a failed ExecuteSyncPlan is reported via ErrorAval and leaves the selection untouched for a retry`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()

    let syncApi =
        InvoiceSyncApiSpy(
            GetSyncPlanResult =
                Ok
                    { StatusByInvoiceId = Map.empty
                      Plan = [ aSyncPlanRow (Some "INV-1") "INV-1" CreateSyncAction ]
                      OrphanedEvents = []
                      NotReadyReason = None },
            ExecuteSyncPlanResult = Error(failure "Google rejected the request")
        )

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    invoicesModule.ToggleInvoice "INV-1"
    invoicesModule.ExecuteSync()

    Assert.Equal(Some "Google rejected the request", AVal.force invoicesModule.ErrorAval)
    Assert.Equal<Set<string>>(Set.ofList [ "INV-1" ], AVal.force invoicesModule.SelectedInvoiceIdsAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed sync-plan read on the initial load is reported via ErrorAval`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()
    let syncApi = InvoiceSyncApiSpy(GetSyncPlanResult = Error(failure "Google is unreachable"))

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    Assert.Equal(Some "Google is unreachable", AVal.force invoicesModule.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``an invoice that cannot become a calendar event never carries a sync status, whatever the plan says`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)

    let invoiceApi =
        InvoiceApiSpy(GetInvoicesFor = (fun _ -> [ anInvoice "INV-NODUE" ]))

    let syncApi =
        InvoiceSyncApiSpy(
            GetSyncPlanResult =
                Ok
                    { StatusByInvoiceId = Map.ofList [ "INV-NODUE", UpToDateSync ]
                      Plan = []
                      OrphanedEvents = []
                      NotReadyReason = None }
        )

    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api syncApi.Api

    let renderedInvoice = AVal.force invoicesModule.InvoicesAval |> List.exactlyOne

    Assert.False(renderedInvoice.CanBecomeCalendarEvent)
    Assert.Equal(None, renderedInvoice.SyncStatus)

// ---- /settings/scan-windows: the same class, and this module creator had no test at all ----

[<Fact; Trait("Level", "Unit")>]
let ``the scan-windows browser loads the windows and marks the remembered choice`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 90)

    let scanWindowsBrowserModule =
        InvoicesModuleCreators.getScanWindowsBrowserModule runSynchronously windowApi.Api

    Assert.Equal<int list>([ 7; 14; 90 ], AVal.force scanWindowsBrowserModule.WindowsAval |> List.map (fun window -> window.Days))
    Assert.Equal(90, AVal.force scanWindowsBrowserModule.SelectedWindowDaysAval)
    Assert.Equal(None, AVal.force scanWindowsBrowserModule.ErrorAval)
    Assert.False(AVal.force scanWindowsBrowserModule.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``the scan-windows browser reports a failed selected-window read rather than opening on nothing`` () =
    let windowApi =
        ScanWindowApiSpy(GetSelectedResult = Error(failure "the settings row is unreachable"))

    let scanWindowsBrowserModule =
        InvoicesModuleCreators.getScanWindowsBrowserModule runSynchronously windowApi.Api

    // the list loaded, so without an alert the page just marks nothing as current
    Assert.Equal<int list>([ 7; 14; 90 ], AVal.force scanWindowsBrowserModule.WindowsAval |> List.map (fun window -> window.Days))
    Assert.Equal(0, AVal.force scanWindowsBrowserModule.SelectedWindowDaysAval)
    Assert.Equal(Some "the settings row is unreachable", AVal.force scanWindowsBrowserModule.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``the module creator uses no Async.Start`` () =
    let path =
        System.IO.Path.GetFullPath(
            System.IO.Path.Combine(
                __SOURCE_DIRECTORY__,
                "..", "..", "..",
                "MyDogsbody.UI.Portal", "ModuleCreators", "InvoicesModuleCreators.fs"
            )
        )

    Assert.DoesNotContain("Async.Start", System.IO.File.ReadAllText path)
