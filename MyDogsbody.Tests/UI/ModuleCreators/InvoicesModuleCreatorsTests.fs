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
      CannotUploadReason = Some "no due date" }

let private aWindow days : ScanWindowUiType =
    { Id = string days; Days = days; Label = $"mail received in the last {days} days" }

type private ScanWindowApiSpy() =
    member val SelectedCalls: int list = [] with get, set
    member val GetSelectedResult: Result<int, MyDogsbodyException> = Ok 14 with get, set

    member this.Api: ScanWindowApi =
        { GetScanWindows = fun () -> Ok [ aWindow 7; aWindow 14; aWindow 90 ]
          AddScanWindow = fun _ -> Ok()
          DeleteScanWindow = fun _ -> Ok()
          GetSelectedScanWindow = fun () -> this.GetSelectedResult
          SelectScanWindow =
            fun days ->
                this.SelectedCalls <- this.SelectedCalls @ [ days ]
                Ok() }

type private InvoiceApiSpy() =
    member val ScanCalls: int list = [] with get, set
    member val GetInvoicesCalls: int list = [] with get, set
    member val GetProblemsCalls: int = 0 with get, set
    member val ScanResult: Result<ScanResultUiType, MyDogsbodyException> = Ok { Invoices = []; Problems = [] } with get, set
    /// Per-window ledger contents, so a test can prove narrowing re-queries for the smaller window.
    member val GetInvoicesFor: int -> InvoiceUiType list = (fun _ -> []) with get, set

    member this.Api: InvoiceApi =
        { Scan =
            fun days ->
                this.ScanCalls <- this.ScanCalls @ [ days ]
                this.ScanResult
          GetInvoices =
            fun days ->
                this.GetInvoicesCalls <- this.GetInvoicesCalls @ [ days ]
                Ok(this.GetInvoicesFor days)
          DeleteInvoice = fun _ -> Ok()
          GetProblems =
            fun () ->
                this.GetProblemsCalls <- this.GetProblemsCalls + 1
                Ok []
          GetTombstones = fun () -> Ok []
          UndeleteInvoice = fun _ _ -> Ok() }

[<Fact; Trait("Level", "Unit")>]
let ``the initial window comes from the API's resolved choice, never a literal 14`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 90)
    let invoiceApi = InvoiceApiSpy()

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    Assert.Equal(90, AVal.force m.SelectedWindowDaysAval)
    // it scanned exactly the resolved window
    Assert.Equal<int list>([ 90 ], invoiceApi.ScanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``selecting a window persists the choice and reloads the ledger WITHOUT scanning the mailbox`` () =
    // Q1.9 fallback (12.4 measured a full re-read at ~60 s): a window change filters the ledger
    // that is already stored, it does not read the mailbox again. A scan is m.Rescan only.
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    m.SelectWindow 7

    Assert.Equal<int list>([ 7 ], windowApi.SelectedCalls)
    Assert.Equal(7, AVal.force m.SelectedWindowDaysAval)
    // the ledger was re-queried for the new window...
    Assert.Contains(7, invoiceApi.GetInvoicesCalls)
    // ...and the mailbox was NOT scanned: only the initial load's scan (14) ever ran
    Assert.Equal<int list>([ 14 ], invoiceApi.ScanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``narrowing the window re-queries the ledger for the smaller window`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 90)

    let invoiceApi =
        InvoiceApiSpy(GetInvoicesFor = (fun days -> if days >= 90 then [ anInvoice "OLD"; anInvoice "NEW" ] else [ anInvoice "NEW" ]))

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    m.SelectWindow 7
    Assert.Equal<string list>([ "NEW" ], AVal.force m.InvoicesAval |> List.map (fun i -> i.Reference))

    // widening brings the out-of-window invoice back - it was hidden, not forgotten
    m.SelectWindow 90
    Assert.Equal<string list>([ "OLD"; "NEW" ], AVal.force m.InvoicesAval |> List.map (fun i -> i.Reference))

[<Fact; Trait("Level", "Unit")>]
let ``Rescan reads the mailbox for the current window`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    m.Rescan()

    // the initial load's scan (30) then the explicit Rescan (30)
    Assert.Equal<int list>([ 30; 30 ], invoiceApi.ScanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``a scan failure sets ErrorAval; a later success clears it`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy(ScanResult = Error(failure "the store is unreachable"))

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    Assert.Equal(Some "the store is unreachable", AVal.force m.ErrorAval)

    invoiceApi.ScanResult <- Ok { Invoices = [ anInvoice "INV-1" ]; Problems = [] }
    m.Rescan()

    Assert.Equal(None, AVal.force m.ErrorAval)
    Assert.Equal(1, (AVal.force m.InvoicesAval).Length)

[<Fact; Trait("Level", "Unit")>]
let ``deleting an invoice reloads the current window WITHOUT scanning the mailbox`` () =
    // The row is hard-deleted, so GetInvoices no longer returns it - a full scan (~60 s on a wide
    // window) to confirm that would be the exact surprise the Q1.9 fallback removes.
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    m.DeleteInvoice "INV-9"

    // the ledger was re-queried for the current window...
    Assert.Equal<int list>([ 30; 30 ], invoiceApi.GetInvoicesCalls) // initial load + after delete
    // ...and only the initial load ever scanned the mailbox
    Assert.Equal<int list>([ 30 ], invoiceApi.ScanCalls)

[<Fact; Trait("Level", "Unit")>]
let ``un-deleting an invoice scans the mailbox, since only a scan can restore the removed row`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    m.UndeleteInvoice "1" "INV-9"

    // initial load (30) then the rescan a restore needs (30)
    Assert.Equal<int list>([ 30; 30 ], invoiceApi.ScanCalls)

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
