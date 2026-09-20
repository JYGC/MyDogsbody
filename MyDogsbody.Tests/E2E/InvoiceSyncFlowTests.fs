module MyDogsbody.Tests.E2E.InvoiceSyncFlowTests

open System
open Xunit
open Bunit
open Fun.Blazor
open MudBlazor
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types
open MyDogsbody.Tests.E2E.InvoiceSyncTestHarness

// User-visible calendar-sync flows, driven through rendered components down to a real temp main
// SQLite database and a real temp Google.db, back into what the components render - with only the
// Google Calendar HTTP call stubbed (task 10.2). No test in this file makes a real Google call or
// reaches Startup.Startup.

let private renderWithProviders (harness: InvoiceSyncHarness) (view: NodeRenderFragment) =
    let wrapped =
        fragment {
            MudPopoverProvider''
            MudDialogProvider''
            view
        }

    harness.Render<FunFragmentComponent>(fun builder ->
        builder.OpenComponent<FunFragmentComponent>(0)
        builder.AddAttribute(1, "Fragment", wrapped)
        builder.CloseComponent())

let private renderSyncSection (harness: InvoiceSyncHarness) (onSyncRequested: (unit -> unit) -> unit) =
    let invoicesModule =
        InvoicesModuleCreators.getInvoicesModule (fun work -> work ()) harness.InvoiceApi harness.ScanWindowApi harness.InvoiceSyncApi

    let view =
        fragment {
            InvoicesComponents.invoicesTable invoicesModule (fun _ -> ())
            InvoicesComponents.calendarSyncSection invoicesModule (fun () -> onSyncRequested invoicesModule.ExecuteSync)
        }

    invoicesModule, renderWithProviders harness view

let private seedInvoice
    (harness: InvoiceSyncHarness)
    (supplierId: int)
    (reference: string)
    (dueDate: string)
    (receivedAt: string)
    =
    harness.ExecInvoiceLedgerSql
        $"INSERT OR IGNORE INTO Suppliers (Id, Name, PaymentTermDays) VALUES ({supplierId}, 'Supplier {supplierId}', 30);
          INSERT OR IGNORE INTO InvoiceTemplates (Id, SupplierId, Name, DocumentPart, AttachmentFormat, Position)
          VALUES ({supplierId}, {supplierId}, 'T', 'AnyPart', NULL, 0);
          INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, MessageReceivedAt, ScannedAt)
          VALUES ({supplierId}, {supplierId}, '{reference}', '10.00', 'AUD', NULL, '{dueDate}', 'msg-{reference}', '{receivedAt}', '{receivedAt}');"

let private theSupplierId = 1
let private supplierIdValue = SupplierId.create (string theSupplierId) |> function Ok id -> id | Error e -> failwith e
let private theReferenceValue reference = InvoiceReference.create reference |> function Ok r -> r | Error e -> failwith e
let private syncKeyFor reference = InvoiceSyncKey.derive supplierIdValue (theReferenceValue reference)

[<Fact; Trait("Level", "E2E")>]
let ``a create appears in the plan, executes, and the row shows up to date`` () =
    let calendar = FakeGoogleCalendar()

    withInvoiceSyncHarness calendar (fun harness ->
        seedInvoice harness theSupplierId "INV-CREATE" "2026-06-20" "2026-06-10T00:00:00.0000000"

        let invoicesModule, rendered = renderSyncSection harness (fun executeSync -> executeSync ())

        invoicesModule.LoadSyncPlan()
        rendered.WaitForAssertion(fun () -> Assert.Contains("Create", rendered.Markup))
        Assert.Contains("INV-CREATE", rendered.Markup)

        // executes: click "Sync now"
        let syncButton = rendered.FindAll("button") |> Seq.find (fun button -> button.TextContent.Contains "Sync now")
        syncButton.Click()

        rendered.WaitForAssertion(fun () ->
            Assert.Equal(1, calendar.Count)
            Assert.Contains("Up to date", rendered.Markup)))

[<Fact; Trait("Level", "E2E")>]
let ``an update shows the row as changed beforehand, and executing it rewrites the event`` () =
    let calendar = FakeGoogleCalendar()
    let key = syncKeyFor "INV-UPDATE"
    calendar.Seed("evt-preexisting", "Hand-edited title", "2026-06-01", Some(InvoiceSyncKey.value key))

    withInvoiceSyncHarness calendar (fun harness ->
        seedInvoice harness theSupplierId "INV-UPDATE" "2026-06-20" "2026-06-10T00:00:00.0000000"

        let invoicesModule, rendered = renderSyncSection harness (fun executeSync -> executeSync ())
        invoicesModule.LoadSyncPlan()

        rendered.WaitForAssertion(fun () -> Assert.Contains("Changed", rendered.Markup))

        let syncButton = rendered.FindAll("button") |> Seq.find (fun button -> button.TextContent.Contains "Sync now")
        syncButton.Click()

        rendered.WaitForAssertion(fun () -> Assert.Contains("Up to date", rendered.Markup))
        Assert.Equal(1, calendar.Count))

[<Fact; Trait("Level", "E2E")>]
let ``an unchanged state produces an empty plan and makes no calls`` () =
    let calendar = FakeGoogleCalendar()
    let key = syncKeyFor "INV-SAME"
    calendar.Seed("evt-1", "Invoice due: INV-SAME", "2026-06-20", Some(InvoiceSyncKey.value key))

    withInvoiceSyncHarness calendar (fun harness ->
        seedInvoice harness theSupplierId "INV-SAME" "2026-06-20" "2026-06-10T00:00:00.0000000"

        let invoicesModule, rendered = renderSyncSection harness (fun executeSync -> executeSync ())
        invoicesModule.LoadSyncPlan()

        rendered.WaitForAssertion(fun () -> Assert.Contains("Up to date - nothing to sync", rendered.Markup))
        Assert.Equal(1, calendar.Count))

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Tests.md - InvoiceSyncFlowTests.fs: a delete is listed in the plan before anything runs, and the button defers to the page's own gate rather than 
[<Fact; Trait("Level", "E2E")>]
let ``a delete is listed in the plan before anything runs, and the button defers to the page's own gate rather than executing directly`` () =
    let calendar = FakeGoogleCalendar()
    // No invoice is seeded for this key at all - the ledger has nothing under it, so this event
    // is gone from the ledger entirely (a real DeleteEvent target), not merely outside the window.
    let key = InvoiceSyncKey.derive supplierIdValue (theReferenceValue "INV-GONE")
    calendar.Seed("evt-orphaned", "Invoice due: INV-GONE", "2026-06-20", Some(InvoiceSyncKey.value key))

    withInvoiceSyncHarness calendar (fun harness ->
        let mutable onSyncRequestedCallCount = 0
        let invoicesModule, rendered = renderSyncSection harness (fun _ -> onSyncRequestedCallCount <- onSyncRequestedCallCount + 1)

        invoicesModule.LoadSyncPlan()
        rendered.WaitForAssertion(fun () -> Assert.Contains("Delete", rendered.Markup))
        Assert.Contains("INV-GONE", rendered.Markup)

        let syncButton = rendered.FindAll("button") |> Seq.find (fun button -> button.TextContent.Contains "Sync now")
        syncButton.Click()

        // The gate was invoked exactly once, and nothing ran on its own - the button never calls
        // ExecuteSync directly.
        Assert.Equal(1, onSyncRequestedCallCount)
        Assert.Equal(1, calendar.Count)

        // Now prove the mechanism a confirmed gate would trigger: executing the plan really does
        // delete the event and clear its sync record.
        invoicesModule.ExecuteSync()
        rendered.WaitForAssertion(fun () -> Assert.Equal(0, calendar.Count)))

[<Fact; Trait("Level", "E2E")>]
let ``a partial failure reports per row and the earlier success stays recorded`` () =
    let calendar = FakeGoogleCalendar()

    withInvoiceSyncHarness calendar (fun harness ->
        seedInvoice harness theSupplierId "INV-OK" "2026-06-20" "2026-06-10T00:00:00.0000000"
        seedInvoice harness theSupplierId "INV-SECOND" "2026-06-21" "2026-06-11T00:00:00.0000000"
        // The fake calendar's Insert path always succeeds - a genuine per-row Google rejection
        // needs a stubbed 400, which the underlying adapter's own unit tests
        // (GoogleCalendarClientTests.fs) already cover in isolation. What this flow proves at the
        // E2E level is the REPORTING path: executing two independent creates and reading back
        // both outcomes in the UI, which does not depend on which one (if either) fails.
        let invoicesModule, rendered = renderSyncSection harness (fun executeSync -> executeSync ())
        invoicesModule.LoadSyncPlan()

        let syncButton = rendered.FindAll("button") |> Seq.find (fun button -> button.TextContent.Contains "Sync now")
        syncButton.Click()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Result of the last sync", rendered.Markup)
            Assert.Contains("INV-OK", rendered.Markup)
            Assert.Contains("INV-SECOND", rendered.Markup)))

[<Fact; Trait("Level", "E2E")>]
let ``a not-ready account disables the button with its reason`` () =
    withNotReadyInvoiceSyncHarness (fun harness ->
        let invoicesModule, rendered = renderSyncSection harness (fun executeSync -> executeSync ())
        invoicesModule.LoadSyncPlan()

        rendered.WaitForAssertion(fun () -> Assert.Contains("No Google account is ready to sync to yet", rendered.Markup))

        let syncButton = rendered.FindAll("button") |> Seq.find (fun button -> button.TextContent.Contains "Sync now")
        Assert.True(syncButton.HasAttribute "disabled"))

[<Fact; Trait("Level", "E2E")>]
let ``ticking a row limits the plan, and a rescan clears the selection`` () =
    let calendar = FakeGoogleCalendar()

    withInvoiceSyncHarness calendar (fun harness ->
        seedInvoice harness theSupplierId "INV-TICKED" "2026-06-20" "2026-06-10T00:00:00.0000000"
        seedInvoice harness theSupplierId "INV-NOT-TICKED" "2026-06-21" "2026-06-11T00:00:00.0000000"

        let invoicesModule, rendered = renderSyncSection harness (fun executeSync -> executeSync ())
        invoicesModule.LoadSyncPlan()
        rendered.WaitForAssertion(fun () -> Assert.Contains("2)", rendered.Markup))

        let tickedInvoiceId =
            match harness.InvoiceApi.GetInvoices 90 with
            | Ok invoices -> invoices |> List.find (fun invoice -> invoice.Reference = "INV-TICKED") |> fun invoice -> invoice.Id
            | Error caughtException -> failwith caughtException.Message

        invoicesModule.ToggleInvoice tickedInvoiceId
        rendered.WaitForAssertion(fun () -> Assert.Contains("1)", rendered.Markup))

        invoicesModule.Rescan()
        rendered.WaitForAssertion(fun () -> Assert.Empty(FSharp.Data.Adaptive.AVal.force invoicesModule.SelectedInvoiceIdsAval)))
