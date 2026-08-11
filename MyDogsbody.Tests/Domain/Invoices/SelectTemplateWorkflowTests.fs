module MyDogsbody.Tests.Domain.Invoices.SelectTemplateWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private testSupplierId = SupplierId.create "1" |> valueOrFail
let private paymentTerm = PaymentTermDays.create 0 |> valueOrFail

let private storedTemplate id (rules: TemplateFieldRule list) (part: DocumentPart) : StoredTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = "1"; Name = $"Template {id}"; Part = part; Position = 0; Rules = rules }

    {
        Id = TemplateId.create id |> valueOrFail
        Template =
            match ValidateTemplateWorkflow.validateTemplate unvalidated with
            | Ok template -> template
            | Error error -> failwith $"Test setup produced an invalid template: {error}"
    }

let private line blockIndex text : TextLine = { Text = text; BlockIndex = blockIndex }

let private message (parts: (MessagePart * TextLine list) list) : ScannedMessage =
    {
        SourceMessageId = SourceMessageId.create "msg-1" |> valueOrFail
        Sender = "billing@acme.example"
        Subject = ""
        ReceivedAt = DateTime(2026, 7, 14)
        Parts = parts
    }

let private stableAmountRule = { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
let private stableCurrencyRule = { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
let private stableAmountLine = line 0 "Total: 100.00"

let private select templates scanned =
    SelectTemplateWorkflow.selectTemplate paymentTerm testSupplierId templates scanned

[<Fact; Trait("Level", "Unit")>]
let ``selectTemplate tries templates in stored order and the first complete match wins`` () =
    let first =
        storedTemplate
            "1"
            [ { Field = Reference; Rule = AfterLabel "Ref A:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let second =
        storedTemplate
            "2"
            [ { Field = Reference; Rule = AfterLabel "Ref B:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    // both would succeed if tried against text carrying both labels, but stored order says
    // template 1 goes first and it alone should win here
    let scanned = message [ BodyPart, [ line 0 "Ref A: FIRST"; stableAmountLine ] ]

    let actual = select [ first; second ] scanned

    match actual with
    | Ok invoice ->
        Assert.Equal("FIRST", invoice.Reference)
        Assert.Equal("1", TemplateId.value invoice.TemplateId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``a message matching only the second template still yields an invoice`` () =
    let first =
        storedTemplate
            "1"
            [ { Field = Reference; Rule = AfterLabel "Ref A:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let second =
        storedTemplate
            "2"
            [ { Field = Reference; Rule = AfterLabel "Ref B:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let scanned = message [ BodyPart, [ line 0 "Ref B: SECOND"; stableAmountLine ] ] // no "Ref A:" anywhere

    let actual = select [ first; second ] scanned

    match actual with
    | Ok invoice ->
        Assert.Equal("SECOND", invoice.Reference)
        Assert.Equal("2", TemplateId.value invoice.TemplateId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``when every applicable template fails, the error is the one from the last template tried`` () =
    let first =
        storedTemplate
            "1"
            [ { Field = Reference; Rule = AfterLabel "Ref A:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let second =
        storedTemplate
            "2"
            [ { Field = Reference; Rule = AfterLabel "Ref B:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let scanned = message [ BodyPart, [ stableAmountLine ] ] // matches neither template's reference label

    let actual = select [ first; second ] scanned

    match actual with
    | Error (TemplateMatchedNothing (templateId, field)) ->
        Assert.Equal("2", TemplateId.value templateId) // the LAST one tried, not the first
        Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a template whose document part the message does not carry is skipped, not failed`` () =
    // This template would ALWAYS fail its own Reference rule against this message (no PDF
    // attachment exists at all) - but it targets Attachment Pdf and the message carries no PDF,
    // so it must be skipped before ever being applied, not attempted and reported as a failure.
    let pdfOnly =
        storedTemplate
            "1"
            [ { Field = Reference; Rule = AttachmentName @"^(\d+)\.pdf$"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
            (Attachment Pdf)
    let bodyTemplate =
        storedTemplate
            "2"
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            Body
    let scanned = message [ BodyPart, [ line 0 "Ref: BODY-1"; stableAmountLine ] ] // no attachment at all

    let actual = select [ pdfOnly; bodyTemplate ] scanned

    match actual with
    | Ok invoice ->
        Assert.Equal("BODY-1", invoice.Reference)
        Assert.Equal("2", TemplateId.value invoice.TemplateId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``no template for the parts a message carries reports NoTemplateForSupplier carrying the supplier`` () =
    let pdfOnly =
        storedTemplate
            "1"
            [ { Field = Reference; Rule = AttachmentName @"^(\d+)\.pdf$"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
            (Attachment Pdf)
    let scanned = message [ BodyPart, [ stableAmountLine ] ] // no attachment at all - pdfOnly cannot apply

    let actual = select [ pdfOnly ] scanned

    match actual with
    | Error (NoTemplateForSupplier supplierId) -> Assert.Equal("1", SupplierId.value supplierId)
    | other -> Assert.Fail($"Expected Error(NoTemplateForSupplier _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``several attachments are each tried in turn`` () =
    let template =
        storedTemplate
            "1"
            [ { Field = Reference; Rule = AttachmentName @"^(\d+)\.pdf$"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
            (Attachment Pdf)
    // The template targets Attachment Pdf, so the Amount rule (AfterLabel, searching
    // content.Lines) only sees text from PDF attachment parts - the amount line has to live in
    // one of those, not the body, or Amount finds nothing regardless of the Reference outcome.
    let scanned =
        message
            [ BodyPart, []
              AttachmentPart("cover-letter.pdf", Pdf), []
              AttachmentPart("445566.pdf", Pdf), [ stableAmountLine ] ]

    let actual = select [ template ] scanned

    match actual with
    | Ok invoice -> Assert.Equal("445566", invoice.Reference)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
