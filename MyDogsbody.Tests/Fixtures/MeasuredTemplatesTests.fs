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
let private testSupplierId = SupplierId.create "1" |> valueOrFail

[<Fact; Trait("Level", "Unit")>]
let ``invoice-management platform: every field extracts, and the due date is derived rather than stated anywhere in the document`` () =
    let actual =
        ApplyTemplateWorkflow.applyTemplate
            paymentTermDays30
            testTemplateId
            InvoiceManagementPlatform.template
            InvoiceManagementPlatform.message

    match actual with
    | Ok invoice ->
        Assert.Equal("SYN-1001", invoice.Reference)
        Assert.Equal(245.00m, invoice.Amount)
        Assert.Equal(Some "AUD", invoice.Currency)
        Assert.Equal(Some (DateTime(2026, 7, 14)), invoice.IssueDate)
        Assert.Equal(Some (DateTime(2026, 8, 13)), invoice.DueDate)

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
    let actual =
        ApplyTemplateWorkflow.applyTemplate
            paymentTermDays30
            testTemplateId
            WaterUtility.dueDateTemplate
            WaterUtility.dueDateMessage

    match actual with
    | Ok invoice ->
        Assert.Equal("WU-88213", invoice.Reference)
        Assert.Equal(142.50m, invoice.Amount)
        Assert.Equal(Some "AUD", invoice.Currency)
        Assert.Equal(Some (DateTime(2026, 8, 22)), invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``water utility: SelectTemplateWorkflow reaches the direct-debit template when the Due date template's labels are absent`` () =
    // Both templates stored for the same supplier, Due date template first (stored order) - the
    // direct-debit document carries none of the first template's labels, so it must fail and the
    // second template must be tried and succeed.
    let stored: StoredTemplate list =
        [ { Id = TemplateId.create "T1" |> valueOrFail; Template = WaterUtility.dueDateTemplate }
          { Id = TemplateId.create "T2" |> valueOrFail; Template = WaterUtility.directDebitTemplate } ]

    let actual = SelectTemplateWorkflow.selectTemplate paymentTermDays30 testSupplierId stored WaterUtility.directDebitMessage

    match actual with
    | Ok invoice ->
        Assert.Equal("WU-88214", invoice.Reference)
        Assert.Equal(142.50m, invoice.Amount)
        Assert.Equal(Some (DateTime(2026, 8, 22)), invoice.DueDate)
        Assert.Equal("T2", TemplateId.value invoice.TemplateId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``accounting platform: every field extracts using a different rule kind per field`` () =
    let actual =
        ApplyTemplateWorkflow.applyTemplate paymentTermDays30 testTemplateId AccountingPlatform.template AccountingPlatform.message

    match actual with
    | Ok invoice ->
        Assert.Equal("ACC-77410", invoice.Reference) // LinesAfterLabel
        Assert.Equal(389.20m, invoice.Amount) // RegexCapture
        Assert.Equal(Some "AUD", invoice.Currency) // FixedValue
        Assert.Equal(Some (DateTime(2026, 9, 11)), invoice.DueDate) // AfterLabel
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``energy retailer: every field extracts, including both Date of Issue and Due Date read directly`` () =
    let actual =
        ApplyTemplateWorkflow.applyTemplate paymentTermDays30 testTemplateId EnergyRetailer.template EnergyRetailer.message

    match actual with
    | Ok invoice ->
        Assert.Equal("ER-50291", invoice.Reference)
        Assert.Equal(210.75m, invoice.Amount)
        Assert.Equal(Some "AUD", invoice.Currency)
        Assert.Equal(Some (DateTime(2026, 7, 1)), invoice.IssueDate)
        Assert.Equal(Some (DateTime(2026, 7, 29)), invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``attachment-name variant: the reference is read from the matching attachment's filename`` () =
    let actual =
        ApplyTemplateWorkflow.applyTemplate
            paymentTermDays30
            testTemplateId
            AttachmentNameVariant.template
            AttachmentNameVariant.message

    match actual with
    | Ok invoice ->
        Assert.Equal("9034521", invoice.Reference)
        Assert.Equal(67.00m, invoice.Amount)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``subject variant: the reference is read from the message subject`` () =
    let actual =
        ApplyTemplateWorkflow.applyTemplate paymentTermDays30 testTemplateId SubjectVariant.template SubjectVariant.message

    match actual with
    | Ok invoice ->
        Assert.Equal("REF-66201", invoice.Reference)
        Assert.Equal(88.40m, invoice.Amount)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
