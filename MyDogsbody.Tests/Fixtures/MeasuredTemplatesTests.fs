module MyDogsbody.Tests.Fixtures.MeasuredTemplatesTests

open System
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Tests.Fixtures.MeasuredTemplates

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private testTemplateId = TemplateId.create "T" |> valueOrFail

/// Every fixture message is built by MeasuredTemplates.scannedMessage, which uses this one id.
let private fixtureMessageId = "fixture-msg"

let private apply templateId template message =
    ApplyTemplateWorkflow.applyTemplate paymentTermDays30 templateId template (MessageNormalization.normalizeMessage message)

/// tasks.md 5.2 asks each fixture to assert "every field it claims to extract". The four fields
/// that are the same question for every fixture - who the invoice belongs to, which template read
/// it, which message it came from, and the currency - are asserted here so no fixture can quietly
/// skip them. Asserting SupplierId is what surfaced the selectTemplate supplier-id mismatch the
/// PR #11 review found.
let private assertProvenance (expectedSupplierId: string) (expectedTemplateId: string) (invoice: ExtractedInvoice) =
    Assert.Equal(expectedSupplierId, SupplierId.value invoice.SupplierId)
    Assert.Equal(expectedTemplateId, TemplateId.value invoice.TemplateId)
    Assert.Equal(fixtureMessageId, SourceMessageId.value invoice.SourceMessageId)
    Assert.Equal("AUD", invoice.Currency)

[<Fact; Trait("Level", "Unit")>]
let ``invoice-management platform: every field extracts, and the due date is derived rather than stated anywhere in the document`` () =
    let actual = apply testTemplateId InvoiceManagementPlatform.template InvoiceManagementPlatform.message

    match actual with
    | Ok invoice ->
        Assert.Equal("SYN-1001", invoice.Reference)
        Assert.Equal(245.00m, invoice.Amount)
        Assert.Equal(Some (DateTime(2026, 7, 14)), invoice.IssueDate)
        Assert.Equal(Some (DateTime(2026, 8, 13)), invoice.DueDate)
        assertProvenance "1" "T" invoice

        // The document's own text never states 13 Aug or Aug 13 - the due date is genuinely
        // derived from IssueDate + PaymentTermDays, not read.
        let documentText =
            InvoiceManagementPlatform.message.Parts
            |> List.collect (fun (_, lines) -> lines |> List.map (fun l -> l.Text))
            |> String.concat " "

        Assert.DoesNotContain("13 Aug", documentText)
        Assert.DoesNotContain("Aug 13", documentText)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``water utility: the Due date template extracts every field for a Due date customer`` () =
    let actual = apply testTemplateId WaterUtility.dueDateTemplate WaterUtility.dueDateMessage

    match actual with
    | Ok invoice ->
        Assert.Equal("WU-88213", invoice.Reference)
        Assert.Equal(142.50m, invoice.Amount)
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(Some (DateTime(2026, 8, 22)), invoice.DueDate)
        assertProvenance "2" "T" invoice
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``water utility: SelectTemplateWorkflow reaches the direct-debit template when the Due date template's labels are absent`` () =
    // Both templates stored for the same supplier, Due date template first (stored order) - the
    // direct-debit document carries none of the first template's labels, so it must fail and the
    // second template must be tried and succeed.
    //
    // The supplier id handed to selectTemplate is the water utility's own "2". It used to be
    // "1" here, which read as an ordinary passing test only because nothing asserted
    // invoice.SupplierId - the mismatch the PR #11 review found.
    let waterUtilitySupplierId = SupplierId.create "2" |> valueOrFail

    let stored: StoredTemplate list =
        [ { Id = TemplateId.create "T1" |> valueOrFail; Template = WaterUtility.dueDateTemplate }
          { Id = TemplateId.create "T2" |> valueOrFail; Template = WaterUtility.directDebitTemplate } ]

    let actual =
        SelectTemplateWorkflow.selectTemplate paymentTermDays30 waterUtilitySupplierId stored WaterUtility.directDebitMessage

    match actual with
    | Ok invoice ->
        Assert.Equal("WU-88214", invoice.Reference)
        Assert.Equal(142.50m, invoice.Amount)
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(Some (DateTime(2026, 8, 22)), invoice.DueDate)
        assertProvenance "2" "T2" invoice
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``accounting platform: every field extracts using a different rule kind per field`` () =
    let actual = apply testTemplateId AccountingPlatform.template AccountingPlatform.message

    match actual with
    | Ok invoice ->
        Assert.Equal("ACC-77410", invoice.Reference) // LinesAfterLabel
        Assert.Equal(389.20m, invoice.Amount) // RegexCapture
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(Some (DateTime(2026, 9, 11)), invoice.DueDate) // AfterLabel
        assertProvenance "3" "T" invoice // Currency, via FixedValue
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``energy retailer: every field extracts, including both Date of Issue and Due Date read directly`` () =
    let actual = apply testTemplateId EnergyRetailer.template EnergyRetailer.message

    match actual with
    | Ok invoice ->
        Assert.Equal("ER-50291", invoice.Reference)
        Assert.Equal(210.75m, invoice.Amount)
        Assert.Equal(Some (DateTime(2026, 7, 1)), invoice.IssueDate)
        Assert.Equal(Some (DateTime(2026, 7, 29)), invoice.DueDate)
        assertProvenance "4" "T" invoice
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``attachment-name variant: the reference is read from the matching attachment's filename`` () =
    let actual = apply testTemplateId AttachmentNameVariant.template AttachmentNameVariant.message

    match actual with
    | Ok invoice ->
        Assert.Equal("9034521", invoice.Reference)
        Assert.Equal(67.00m, invoice.Amount)
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(None, invoice.DueDate)
        assertProvenance "5" "T" invoice
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``subject variant: the reference is read from the message subject`` () =
    let actual = apply testTemplateId SubjectVariant.template SubjectVariant.message

    match actual with
    | Ok invoice ->
        Assert.Equal("REF-66201", invoice.Reference)
        Assert.Equal(88.40m, invoice.Amount)
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(None, invoice.DueDate)
        assertProvenance "6" "T" invoice
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
