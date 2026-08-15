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

/// As storedTemplate, but with the supplier and the stored Position spelled out - the two things
/// the PR #11 review found selectTemplate was not actually consulting.
let private storedTemplateFor id supplierId position (rules: TemplateFieldRule list) (part: DocumentPart) : StoredTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = supplierId; Name = $"Template {id}"; Part = part; Position = position; Rules = rules }

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
    //
    // PR #11 review round 3, finding 1: putting both halves in the SAME attachment is what makes
    // this test pass whether the engine pools every matching attachment or applies the template
    // to one at a time. It is still the right test for "the second attachment is reached at all";
    // the test below is the one that tells the two designs apart.
    let scanned =
        message
            [ BodyPart, []
              AttachmentPart("cover-letter.pdf", Pdf), []
              AttachmentPart("445566.pdf", Pdf), [ stableAmountLine ] ]

    let actual = select [ template ] scanned

    match actual with
    | Ok invoice -> Assert.Equal("445566", invoice.Reference)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``an invoice is never assembled out of two different attachments`` () =
    // The halves split across two documents: the cover letter carries an amount, the invoice
    // carries the matching filename. Measured on 6e510c2 this returned Ok with Reference off
    // 445566.pdf and Amount off cover-letter.pdf - a ledger row that exists in neither. Applying
    // the template to one attachment at a time, neither is complete on its own and the LAST
    // failure is reported.
    let template =
        storedTemplate
            "1"
            [ { Field = Reference; Rule = AttachmentName @"^(\d+)\.pdf$"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
            (Attachment Pdf)
    let scanned =
        message
            [ AttachmentPart("cover-letter.pdf", Pdf), [ stableAmountLine ]
              AttachmentPart("445566.pdf", Pdf), [ line 0 "No total on this page" ] ]

    let actual = select [ template ] scanned

    match actual with
    | Error (TemplateMatchedNothing (templateId, field)) ->
        Assert.Equal<TargetField>(Amount, field)
        Assert.Equal("1", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing (_, Amount)), but got {other}")

// PR #11 review, finding 5: stored Position decides which template wins, so the sort is this
// workflow's job and not the store's. ListTemplatesWorkflow makes the same point for display
// order; here the stakes are higher, because the order picks the template rather than the row.

[<Fact; Trait("Level", "Unit")>]
let ``selectTemplate tries templates in stored Position order, not the order the dependency returned them`` () =
    // A LoadTemplatesForSupplier adapter returning rows in insertion or rowid order would
    // otherwise silently override everything ReorderTemplatesWorkflow exists to let the user
    // configure - and the result is a wrong-but-plausible invoice from the wrong template.
    let first =
        storedTemplateFor
            "first-by-position"
            "1"
            0
            [ { Field = Reference; Rule = AfterLabel "Ref A:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let second =
        storedTemplateFor
            "second-by-position"
            "1"
            1
            [ { Field = Reference; Rule = AfterLabel "Ref B:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    // The document carries BOTH labels, so whichever template is tried first is the one that
    // wins - and the list is handed over in the opposite order to the stored positions.
    let scanned = message [ BodyPart, [ line 0 "Ref A: BY-POSITION"; line 0 "Ref B: BY-LIST-ORDER"; stableAmountLine ] ]

    let actual = select [ second; first ] scanned

    match actual with
    | Ok invoice ->
        Assert.Equal("BY-POSITION", invoice.Reference)
        Assert.Equal("first-by-position", TemplateId.value invoice.TemplateId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``two templates sharing a position keep the order the dependency returned them in`` () =
    // List.sortBy is stable, so equal positions are left alone rather than reshuffled - worth
    // pinning, since a supplier's rows can carry duplicate positions after a partial reorder.
    let earlier =
        storedTemplateFor
            "earlier"
            "1"
            7
            [ { Field = Reference; Rule = AfterLabel "Ref A:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let later =
        storedTemplateFor
            "later"
            "1"
            7
            [ { Field = Reference; Rule = AfterLabel "Ref B:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let scanned = message [ BodyPart, [ line 0 "Ref A: FIRST"; line 0 "Ref B: SECOND"; stableAmountLine ] ]

    let actual = select [ earlier; later ] scanned

    match actual with
    | Ok invoice -> Assert.Equal("earlier", TemplateId.value invoice.TemplateId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

// PR #11 review, finding 12: supplierId was read on exactly one line - the error case - so an
// invoice could be filed under the template's supplier rather than the one the caller asked
// about, silently.

[<Fact; Trait("Level", "Unit")>]
let ``selectTemplate ignores a template belonging to another supplier`` () =
    let otherSuppliersTemplate =
        storedTemplateFor
            "belongs-to-2"
            "2"
            0
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let ownTemplate =
        storedTemplateFor
            "belongs-to-1"
            "1"
            1
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let scanned = message [ BodyPart, [ line 0 "Ref: SHARED"; stableAmountLine ] ]

    // testSupplierId is "1"; the other supplier's template sorts first and would otherwise win.
    let actual = select [ otherSuppliersTemplate; ownTemplate ] scanned

    match actual with
    | Ok invoice ->
        Assert.Equal("belongs-to-1", TemplateId.value invoice.TemplateId)
        Assert.Equal("1", SupplierId.value invoice.SupplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``selectTemplate reports NoTemplateForSupplier when every template belongs to another supplier`` () =
    let otherSuppliersTemplate =
        storedTemplateFor
            "belongs-to-2"
            "2"
            0
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let scanned = message [ BodyPart, [ line 0 "Ref: SHARED"; stableAmountLine ] ]

    let actual = select [ otherSuppliersTemplate ] scanned

    match actual with
    | Error (NoTemplateForSupplier supplierId) -> Assert.Equal("1", SupplierId.value supplierId)
    | other -> Assert.Fail($"Expected Error(NoTemplateForSupplier _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``the invoice is filed under the supplier the caller asked about`` () =
    let template =
        storedTemplateFor
            "1"
            "1"
            0
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
            AnyPart
    let scanned = message [ BodyPart, [ line 0 "Ref: OWN-1"; stableAmountLine ] ]

    let actual = select [ template ] scanned

    match actual with
    | Ok invoice -> Assert.Equal("1", SupplierId.value invoice.SupplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
