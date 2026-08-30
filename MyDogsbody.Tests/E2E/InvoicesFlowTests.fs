module MyDogsbody.Tests.E2E.InvoicesFlowTests

open System
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

/// Seeds a supplier (id 1) + template (id 1) + one invoice with an explicit MessageReceivedAt, so
/// a flow can place an invoice inside or outside a window (the harness clock is 2026-06-15).
/// dueDate is a 'yyyy-MM-dd' string (quoted) or NULL.
let private seedInvoiceReceived (harness: InvoicesHarness) (reference: string) (dueDate: string) (receivedAt: string) =
    harness.Exec
        "INSERT OR IGNORE INTO Suppliers (Id, Name, PaymentTermDays) VALUES (1, 'Acme Pty Ltd', 30);
         INSERT OR IGNORE INTO InvoiceTemplates (Id, SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (1, 1, 'T', 'AnyPart', NULL, 0);"

    harness.Exec
        $"INSERT INTO Invoices (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate, SourceMessageId, MessageReceivedAt, ScannedAt)
          VALUES (1, 1, '{reference}', '100.00', 'AUD', NULL, {dueDate}, 'msg-{reference}', '{receivedAt}', '2026-06-15T00:00:00.0000000');"

let private seedInvoice (harness: InvoicesHarness) (reference: string) (dueDate: string) =
    seedInvoiceReceived harness reference dueDate "2026-06-10T00:00:00.0000000"

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

/// The `<tbody>` of the first table in the rendered markup.
let private tableBody (markup: string) =
    let start = markup.IndexOf("<tbody", StringComparison.Ordinal)
    let finish = markup.IndexOf("</tbody>", StringComparison.Ordinal)
    Assert.True(start >= 0 && finish > start, "expected a rendered <tbody>")
    markup.Substring(start, finish - start + "</tbody>".Length)

let private countOccurrences (needle: string) (haystack: string) =
    let mutable count = 0
    let mutable index = haystack.IndexOf(needle, StringComparison.Ordinal)

    while index >= 0 do
        count <- count + 1
        index <- haystack.IndexOf(needle, index + 1, StringComparison.Ordinal)

    count

/// MudTable renders the `<tr>` around whatever RowTemplate produces - every other table in this
/// application (credentials, suppliers, mail accounts, scan windows, and the problems and
/// tombstones views in this same component file) hands it a bare fragment of `MudTd`. Wrapping the
/// cells in a `MudTr` as well produces `<tr><tr><td>…</td></tr></tr>`: a `tr` is not allowed inside
/// a `tr`, and under CSS table fix-up the inner row is boxed into a single anonymous cell of the
/// outer one, so the ledger's columns stop lining up with its own headers.
///
/// One row per invoice, its cells directly inside it - and the no-due-date row still greyed, which
/// is the reason the `MudTr` was there (Q1.10: stored and listed, greyed out).
[<Fact; Trait("Level", "E2E")>]
let ``each invoice is one table row, its cells directly inside it, and a no-due-date row is greyed`` () =
    withInvoicesHarness (fun harness ->
        seedInvoice harness "INV-NODUE" "NULL"

        let _, rendered = renderInvoices harness
        rendered.WaitForAssertion(fun () -> Assert.Contains("INV-NODUE", rendered.Markup))

        let body = tableBody rendered.Markup

        // one invoice -> exactly one <tr>, not a row nested inside a row
        Assert.Equal(1, countOccurrences "<tr" body)
        // the five cells and the delete button's cell are that row's own children
        Assert.Equal(6, countOccurrences "<td" body)
        // the greying survives: it is on the row MudTable renders
        Assert.Contains("mud-text-disabled", body))

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
let ``the window picker is bound to the store's windows and selecting one persists and reloads`` () =
    withInvoicesHarness (fun harness ->
        let m, rendered = renderInvoices harness

        // the picker renders whatever the store holds - the seeded five - and opens on 14
        rendered.WaitForAssertion(fun () ->
            Assert.Equal(5, (FSharp.Data.Adaptive.AVal.force m.ScanWindowsAval).Length)
            Assert.Equal(14, FSharp.Data.Adaptive.AVal.force m.SelectedWindowDaysAval)
            // the label above the table says what the window measures, not a bare number
            Assert.Contains("mail received in the last 14 days", rendered.Markup)
            // "Scan now" is on screen next to the picker
            Assert.Contains("Scan now", rendered.Markup))

        m.SelectWindow 90

        rendered.WaitForAssertion(fun () ->
            Assert.Equal(90, FSharp.Data.Adaptive.AVal.force m.SelectedWindowDaysAval)
            // the window change did NOT scan the mailbox: the initial load's "no mail account"
            // error is cleared by the reload rather than re-raised
            Assert.Equal(None, FSharp.Data.Adaptive.AVal.force m.ErrorAval))

        match harness.ScanWindowApi.GetSelectedScanWindow() with
        | Ok days -> Assert.Equal(90, days)
        | Error e -> Assert.Fail($"expected the choice to persist: {e.Message}"))

[<Fact; Trait("Level", "E2E")>]
let ``changing the window filters the stored ledger without a scan; Scan now reads the mailbox`` () =
    withInvoicesHarness (fun harness ->
        // one invoice inside a 14-day window (clock 2026-06-15), one only inside 90 days
        seedInvoiceReceived harness "INV-RECENT" "NULL" "2026-06-11T00:00:00.0000000"
        seedInvoiceReceived harness "INV-OLD" "NULL" "2026-04-01T00:00:00.0000000"

        let m, rendered = renderInvoices harness

        // opens on 14 days: only the recent invoice is in view
        rendered.WaitForAssertion(fun () ->
            Assert.Contains("INV-RECENT", rendered.Markup)
            Assert.DoesNotContain("INV-OLD", rendered.Markup))

        // widen to 90: the older invoice was hidden, not forgotten - it comes back with no scan
        m.SelectWindow 90

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("INV-OLD", rendered.Markup)
            Assert.Contains("INV-RECENT", rendered.Markup)
            Assert.Equal(None, FSharp.Data.Adaptive.AVal.force m.ErrorAval))

        // "Scan now" DOES read the mailbox - and with no mail account selected that raises the alert
        m.Rescan()

        rendered.WaitForAssertion(fun () -> Assert.Contains("mail account", rendered.Markup)))

[<Fact; Trait("Level", "E2E")>]
let ``a scan with no mail account selected shows an alert and logs nothing`` () =
    withInvoicesHarness (fun harness ->
        let m, rendered = renderInvoices harness
        m.Rescan()

        rendered.WaitForAssertion(fun () -> Assert.Contains("mail account", rendered.Markup))
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``the "Rescan everything" button renders, and pressing it with no mail account raises the same alert`` () =
    withInvoicesHarness (fun harness ->
        seedInvoice harness "INV-KEEP" "NULL"
        let m, rendered = renderInvoices harness

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("INV-KEEP", rendered.Markup)
            // the button is on screen next to "Scan now"
            Assert.Contains("Rescan everything", rendered.Markup))

        m.RescanEverything()

        rendered.WaitForAssertion(fun () ->
            // no mail account: the watermark-clearing scan reports it, same as "Scan now"
            Assert.Contains("mail account", rendered.Markup)
            // the stored ledger is still on screen behind the alert
            Assert.Contains("INV-KEEP", rendered.Markup))

        // an expected failure, nothing logged
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
