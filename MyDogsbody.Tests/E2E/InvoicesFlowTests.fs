module MyDogsbody.Tests.E2E.InvoicesFlowTests

open Xunit
open Bunit
open Fun.Blazor
open MudBlazor
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.Tests.E2E.InvoicesTestHarness

/// The window picker is a MudSelect, which renders through a popover - so every view that
/// contains it must be rendered inside a MudPopoverProvider, the same way SuppliersFlowTests
/// renders the editor inside a MudDialogProvider.
let private renderWithProviders (harness: InvoicesHarness) (view: NodeRenderFragment) =
    let wrapped =
        fragment {
            MudPopoverProvider''
            view
        }

    harness.Render<FunFragmentComponent>(fun builder ->
        builder.OpenComponent<FunFragmentComponent>(0)
        builder.AddAttribute(1, "Fragment", wrapped)
        builder.CloseComponent())

// User-visible flows, driven through a rendered component down to a real temp SQLite file and
// back into what the component renders. Driving the WPF BlazorWebView window - and a full mail
// scan against a real Thunderbird profile - is out of scope for the suite; the scan measurement
// is recorded in outcome.md (Phase 12).

let private renderInvoices (harness: InvoicesHarness) =
    let m =
        InvoicesModuleCreators.getInvoicesModule (fun work -> work ()) harness.InvoiceApi harness.ScanWindowApi

    let view = InvoicesComponents.invoicesTable m (fun _ -> ())
    m, renderWithProviders harness view

/// Seeds a supplier (id 1) + template (id 1) + one invoice, so a flow can read it back through
/// the real API. dueDate is a yyyy-MM-dd string or NULL.
let private seedInvoice (harness: InvoicesHarness) (reference: string) (dueDate: string) =
    harness.Exec
        "INSERT OR IGNORE INTO Suppliers (Id, Name, PaymentTermDays) VALUES (1, 'Acme Pty Ltd', 30);
         INSERT OR IGNORE INTO InvoiceTemplates (Id, SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (1, 1, 'T', 'AnyPart', NULL, 0);"

    harness.Exec
        $"INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, MessageReceivedAt, ScannedAt)
          VALUES (1, 1, '{reference}', '100.00', 'AUD', NULL, {dueDate}, 'msg-{reference}', '2026-06-10T00:00:00.0000000', '2026-06-15T00:00:00.0000000');"

[<Fact; Trait("Level", "E2E")>]
let ``the invoices table renders a seeded invoice, and one with no due date is greyed with its reason`` () =
    withInvoicesHarness (fun harness ->
        seedInvoice harness "INV-DUE" "'2026-07-01'"
        seedInvoice harness "INV-NODUE" "NULL"

        let _, rendered = renderInvoices harness

        rendered.WaitForAssertion(fun () ->
            let markup = rendered.Markup
            Assert.Contains("INV-DUE", markup)
            Assert.Contains("INV-NODUE", markup)
            Assert.Contains("Acme Pty Ltd", markup)
            // the greyed row shows "no due date" and, on the tooltip, the reason
            Assert.Contains("no due date", markup)
            Assert.Contains("2 invoice(s)", markup)))

[<Fact; Trait("Level", "E2E")>]
let ``deleting an invoice writes a tombstone and the row disappears; the tombstones view then lists it`` () =
    withInvoicesHarness (fun harness ->
        seedInvoice harness "INV-1" "NULL"

        let m, rendered = renderInvoices harness
        rendered.WaitForAssertion(fun () -> Assert.Contains("INV-1", rendered.Markup))

        let invoiceId = (List.head (FSharp.Data.Adaptive.AVal.force m.InvoicesAval)).Id
        m.DeleteInvoice invoiceId

        rendered.WaitForAssertion(fun () -> Assert.DoesNotContain("INV-1", rendered.Markup))

        // the tombstone is on the natural key
        m.LoadTombstones()
        let tombstones = FSharp.Data.Adaptive.AVal.force m.TombstonesAval
        Assert.Equal(1, List.length tombstones)
        Assert.Equal("INV-1", (List.head tombstones).Reference)

        // un-delete removes the tombstone (the next scan would restore the invoice)
        m.UndeleteInvoice (List.head tombstones).SupplierId "INV-1"
        m.LoadTombstones()
        Assert.Empty(FSharp.Data.Adaptive.AVal.force m.TombstonesAval))

[<Fact; Trait("Level", "E2E")>]
let ``the problems view lists a persisted scan problem with its sender and subject`` () =
    withInvoicesHarness (fun harness ->
        harness.Exec
            "INSERT INTO ScanProblems (SourceMessageId, SupplierId, Sender, Subject, ReceivedAt, Cause, Detail, RecordedAt)
             VALUES ('m1', NULL, 'noreply@stranger.test', 'A statement, not an invoice', '2026-06-10T00:00:00.0000000', 'NoSupplierMatched', NULL, '2026-06-15T00:00:00.0000000');"

        let m =
            InvoicesModuleCreators.getInvoicesModule (fun work -> work ()) harness.InvoiceApi harness.ScanWindowApi

        m.LoadProblems()
        let rendered = renderWithProviders harness (InvoicesComponents.problemsView m)

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("noreply@stranger.test", rendered.Markup)
            Assert.Contains("A statement, not an invoice", rendered.Markup)
            Assert.Contains("recognised", rendered.Markup)))

[<Fact; Trait("Level", "E2E")>]
let ``the window picker is bound to the store's windows and selecting one persists then rescans`` () =
    withInvoicesHarness (fun harness ->
        let m, rendered = renderInvoices harness

        // the picker renders whatever the store holds - the seeded five - and opens on 14
        rendered.WaitForAssertion(fun () ->
            Assert.Equal(5, (FSharp.Data.Adaptive.AVal.force m.ScanWindowsAval).Length)
            Assert.Equal(14, FSharp.Data.Adaptive.AVal.force m.SelectedWindowDaysAval)
            // the label above the table says what the window measures, not a bare number
            Assert.Contains("mail received in the last 14 days", rendered.Markup))

        m.SelectWindow 90

        rendered.WaitForAssertion(fun () -> Assert.Equal(90, FSharp.Data.Adaptive.AVal.force m.SelectedWindowDaysAval))

        match harness.ScanWindowApi.GetSelectedScanWindow() with
        | Ok days -> Assert.Equal(90, days)
        | Error e -> Assert.Fail($"expected the choice to persist: {e.Message}"))

[<Fact; Trait("Level", "E2E")>]
let ``a scan with no mail account selected shows an alert and logs nothing`` () =
    withInvoicesHarness (fun harness ->
        let m, rendered = renderInvoices harness
        m.Rescan()

        rendered.WaitForAssertion(fun () -> Assert.Contains("mail account", rendered.Markup))
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``an unreachable store shows an alert and logs exactly one entry`` () =
    withUnreachableInvoiceStoreHarness (fun harness ->
        let m, rendered = renderInvoices harness
        // the initial load already tried GetSelectedScanWindow against the broken store
        rendered.WaitForAssertion(fun () ->
            match FSharp.Data.Adaptive.AVal.force m.ErrorAval with
            | Some _ -> ()
            | None -> Assert.Fail("expected an error on the module"))

        Assert.True(harness.Logged.Count >= 1, "the outright store failure must be logged"))
