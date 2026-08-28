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
    member val ScanResult: Result<ScanResultUiType, MyDogsbodyException> = Ok { Invoices = []; Problems = [] } with get, set

    member this.Api: InvoiceApi =
        { Scan =
            fun days ->
                this.ScanCalls <- this.ScanCalls @ [ days ]
                this.ScanResult
          GetInvoices = fun _ -> Ok []
          DeleteInvoice = fun _ -> Ok()
          GetProblems = fun () -> Ok []
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
let ``selecting a window persists the choice and then rescans it`` () =
    let windowApi = ScanWindowApiSpy()
    let invoiceApi = InvoiceApiSpy()

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    m.SelectWindow 7

    // persist THEN rescan: SelectScanWindow was called, and Scan followed with the same window
    Assert.Equal<int list>([ 7 ], windowApi.SelectedCalls)
    Assert.Equal(7, AVal.force m.SelectedWindowDaysAval)
    Assert.Contains(7, invoiceApi.ScanCalls)
    // the initial scan (14) then the select (7)
    Assert.Equal<int list>([ 14; 7 ], invoiceApi.ScanCalls)

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
let ``deleting an invoice reloads by rescanning the current window`` () =
    let windowApi = ScanWindowApiSpy(GetSelectedResult = Ok 30)
    let invoiceApi = InvoiceApiSpy()

    let m =
        InvoicesModuleCreators.getInvoicesModule runSynchronously invoiceApi.Api windowApi.Api

    m.DeleteInvoice "INV-9"
    // the initial scan (30) then the reload after delete (30)
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
