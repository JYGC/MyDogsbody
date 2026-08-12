module MyDogsbody.Tests.Domain.Invoices.ApplyTemplateWorkflowTests

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

let private validTemplate (rules: TemplateFieldRule list) : ValidTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = "1"; Name = "Test template"; Part = AnyPart; Position = 0; Rules = rules }

    match ValidateTemplateWorkflow.validateTemplate unvalidated with
    | Ok template -> template
    | Error error -> failwith $"Test setup produced an invalid template: {error}"

let private line blockIndex text : TextLine = { Text = text; BlockIndex = blockIndex }

let private message subject (parts: (MessagePart * TextLine list) list) : ScannedMessage =
    {
        SourceMessageId = SourceMessageId.create "msg-1" |> valueOrFail
        Sender = "billing@acme.example"
        Subject = subject
        ReceivedAt = DateTime(2026, 7, 14)
        Parts = parts
    }

let private bodyMessage subject (lines: TextLine list) =
    message subject [ BodyPart, lines ]

/// The three required fields, each on a simple stable rule, so a test can swap out just the
/// rule under scrutiny (usually Reference) without every other required field also failing.
let private stableAmountRule = { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
let private stableCurrencyRule = { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
let private stableAmountLine = line 0 "Total: 100.00"

let private zeroTermSupplierId = "1"
let private zeroPaymentTerm = PaymentTermDays.create 0 |> valueOrFail
let private testTemplateId = TemplateId.create "T1" |> valueOrFail

let private apply template scanned =
    ApplyTemplateWorkflow.applyTemplate zeroPaymentTerm testTemplateId template scanned

[<Fact; Trait("Level", "Unit")>]
let ``ApplyTemplateWorkflow.applyTemplate takes no dependency parameters`` () =
    // The signature itself is the assertion: PaymentTermDays -> TemplateId -> ValidTemplate ->
    // ScannedMessage -> Result<...>, nothing else - no getter, no clock, no network. TemplateId
    // is plain input data, not a dependency function type, so it does not violate that; it is
    // here because ExtractedInvoice.TemplateId and InvoiceError's template-carrying cases need
    // one and ValidTemplate itself carries none - a gap in design.md's listed signature.
    let template = validTemplate [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let result = apply template (bodyMessage "" [ stableAmountLine ])
    Assert.True(Result.isOk result)

[<Fact; Trait("Level", "Unit")>]
let ``AfterLabel returns the remainder of the first normalized line containing the label`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Invoice: INV-1042"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice ->
        Assert.Equal("INV-1042", invoice.Reference)
        Assert.Equal(100.00m, invoice.Amount)
        Assert.Equal(Some "AUD", invoice.Currency)
        Assert.Equal(zeroTermSupplierId, SupplierId.value invoice.SupplierId)
        Assert.Equal("T1", TemplateId.value invoice.TemplateId)
        Assert.Equal("msg-1", SourceMessageId.value invoice.SourceMessageId)
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(None, invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``LinesAfterLabel returns the whole content line the given offset below the label`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = LinesAfterLabel("Invoice number", 1); Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Invoice number"; line 0 "INV-2099"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal("INV-2099", invoice.Reference)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``RegexCapture returns the first capture group of the first match`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = RegexCapture @"INV-(\d+)"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Reference is INV-3344 for your records"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal("3344", invoice.Reference)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``FixedValue returns that value without consulting the text at all`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "N/A"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ stableAmountLine ] // no reference-shaped text anywhere

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal("N/A", invoice.Reference)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``SubjectCapture runs against the message subject, not the document body`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = SubjectCapture @"Ref (\w+)"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "Ref XY99 attached" [ stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal("XY99", invoice.Reference)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``AttachmentName runs against the attachment's filename, not its content`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = AttachmentName @"^(\d+)\.pdf$"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned =
        message
            ""
            [ BodyPart, [ stableAmountLine ]
              AttachmentPart("778899.pdf", Pdf), [ line 0 "irrelevant content" ] ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal("778899", invoice.Reference)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``a label appearing twice takes the first occurrence`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Ref: FIRST"; line 0 "Ref: SECOND"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal("FIRST", invoice.Reference)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``an offset past the end of a block reports the rule found nothing`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = LinesAfterLabel("Invoice number", 5); Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Invoice number"; line 0 "INV-1"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Error (TemplateMatchedNothing (_, field)) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a required field's rule finding nothing reports TemplateMatchedNothing naming the field`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ stableAmountLine ] // no "Invoice:" label anywhere

    let actual = apply template scanned

    match actual with
    | Error (TemplateMatchedNothing (templateId, field)) ->
        Assert.Equal<TargetField>(Reference, field)
        Assert.Equal("T1", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

// Task 4.6: internal whitespace folding, so "1234 5678 90" and "1234567890" are one value.

[<Fact; Trait("Level", "Unit")>]
let ``a reference with internal whitespace from a PDF and a filename without it produce the same value`` () =
    let fromPdfTemplate =
        validTemplate [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let fromFilenameTemplate =
        validTemplate
            [ { Field = Reference; Rule = AttachmentName @"^(\d+)\.pdf$"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]

    let fromPdf = apply fromPdfTemplate (bodyMessage "" [ line 0 "Ref: 1234 5678 90"; stableAmountLine ])
    let fromFilename =
        apply
            fromFilenameTemplate
            (message "" [ BodyPart, [ stableAmountLine ]; AttachmentPart("1234567890.pdf", Pdf), [] ])

    match fromPdf, fromFilename with
    | Ok pdfInvoice, Ok filenameInvoice ->
        Assert.Equal("1234567890", pdfInvoice.Reference)
        Assert.Equal(pdfInvoice.Reference, filenameInvoice.Reference)
    | _ -> Assert.Fail($"Expected both Ok, got {fromPdf} and {fromFilename}")

// Task 4.2: parse hints.

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Total: $1,234.56", 1234.56)>]
[<InlineData("Total: $1,234.56 CR", 1234.56)>]
[<InlineData("Total: $1,234.56 DR", 1234.56)>]
[<InlineData("Total: -50.00", -50.00)>]
[<InlineData("Total: 0.00", 0.00)>]
let ``AsMoney strips currency symbols, thousands separators and a trailing CR or DR suffix`` (rawLine: string, expected: float) =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 rawLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal(decimal expected, invoice.Amount)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``AsMoney with a comma decimal separator parses European-style amounts`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney ',' }
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Total: 1.234,56" ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal(1234.56m, invoice.Amount)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``AmountUnparseable carries the raw text`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Total: not-a-number" ]

    let actual = apply template scanned

    match actual with
    | Error (AmountUnparseable (field, raw)) ->
        Assert.Equal<TargetField>(Amount, field)
        Assert.Equal("not-a-number", raw)
    | other -> Assert.Fail($"Expected Error(AmountUnparseable _), but got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("14 Jul 2026", "d MMM yyyy", 2026, 7, 14)>]
[<InlineData("14/7/2026", "d/M/yyyy", 2026, 7, 14)>]
[<InlineData("14/7/26", "d/M/yy", 2026, 7, 14)>]
[<InlineData("Jul 14, 2026", "MMM d, yyyy", 2026, 7, 14)>]
let ``AsDate parses each of the four measured formats`` (raw: string, format: string, year: int, month: int, day: int) =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate format } ]
    let scanned = bodyMessage "" [ line 0 $"Date: {raw}"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal(Some (DateTime(year, month, day)), invoice.IssueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``DateUnparseable carries the raw text and the format expected`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" } ]
    let scanned = bodyMessage "" [ line 0 "Date: not-a-date"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Error (DateUnparseable (field, raw, format)) ->
        Assert.Equal<TargetField>(IssueDate, field)
        Assert.Equal("not-a-date", raw)
        Assert.Equal("d MMM yyyy", format)
    | other -> Assert.Fail($"Expected Error(DateUnparseable _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``the same text reads as 2 August under d slash M slash yyyy`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d/M/yyyy" } ]
    let scanned = bodyMessage "" [ line 0 "Date: 02/08/2016"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal(Some (DateTime(2016, 8, 2)), invoice.IssueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``the same text reads as 8 February under M slash d slash yyyy - a six-month error if this is ever wrong`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "M/d/yyyy" } ]
    let scanned = bodyMessage "" [ line 0 "Date: 02/08/2016"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal(Some (DateTime(2016, 2, 8)), invoice.IssueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

// Task 4.3: DateFromField - the 12% -> 39% rule.

let private dateFromFieldTemplate () =
    validTemplate
        [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
          stableAmountRule
          stableCurrencyRule
          { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" }
          { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ]

[<Fact; Trait("Level", "Unit")>]
let ``DateFromField IssueDate with a 30 day term and an issue date of 14 July gives 13 August`` () =
    let template = dateFromFieldTemplate ()
    let scanned = bodyMessage "" [ line 0 "Date: 14 Jul 2026"; stableAmountLine ]
    let thirtyDayTerm = PaymentTermDays.create 30 |> valueOrFail

    let actual = ApplyTemplateWorkflow.applyTemplate thirtyDayTerm testTemplateId template scanned

    match actual with
    | Ok invoice -> Assert.Equal(Some (DateTime(2026, 8, 13)), invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``DateFromField with a term of zero gives the issue date itself`` () =
    let template = dateFromFieldTemplate ()
    let scanned = bodyMessage "" [ line 0 "Date: 14 Jul 2026"; stableAmountLine ]

    let actual = apply template scanned // zeroPaymentTerm

    match actual with
    | Ok invoice -> Assert.Equal(Some (DateTime(2026, 7, 14)), invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``DateFromField whose source yields nothing makes the due date absent rather than wrong`` () =
    let template = dateFromFieldTemplate ()
    let scanned = bodyMessage "" [ stableAmountLine ] // no "Date:" label - IssueDate finds nothing

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal(None, invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``the payment term comes from the supplier, so two different templates derive the same due date from the same document`` () =
    let templateA = dateFromFieldTemplate ()
    let templateB =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "Y"; Hint = AsText } // a different rule, same supplier/term
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" }
              { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ]
    let scanned = bodyMessage "" [ line 0 "Date: 14 Jul 2026"; stableAmountLine ]
    let thirtyDayTerm = PaymentTermDays.create 30 |> valueOrFail

    let actualA = ApplyTemplateWorkflow.applyTemplate thirtyDayTerm testTemplateId templateA scanned
    let actualB = ApplyTemplateWorkflow.applyTemplate thirtyDayTerm testTemplateId templateB scanned

    match actualA, actualB with
    | Ok invoiceA, Ok invoiceB -> Assert.Equal(invoiceA.DueDate, invoiceB.DueDate)
    | _ -> Assert.Fail($"Expected both Ok, got {actualA} and {actualB}")

// Task 4.4: RuleTimedOut at apply time.

/// (?=(a+)+b) forces the backtracking fallback (NonBacktracking rejects the lookahead) and is
/// catastrophic under it - verified empirically while building compilePattern's own tests.
let private pathologicalPattern = @"(?=(a+)+b)"
let private longNonMatchingText = String('a', 40) + "!"

[<Fact; Trait("Level", "Unit")>]
let ``a pathological pattern on a required field fails that rule with RuleTimedOut inside the timeout`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = RegexCapture pathologicalPattern; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 longNonMatchingText; stableAmountLine ]
    let stopwatch = Diagnostics.Stopwatch.StartNew()

    let actual = apply template scanned

    stopwatch.Stop()
    Assert.True(stopwatch.ElapsedMilliseconds < 2000L, $"Took {stopwatch.ElapsedMilliseconds}ms")

    match actual with
    | Error (RuleTimedOut (templateId, field)) ->
        Assert.Equal<TargetField>(Reference, field)
        Assert.Equal("T1", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(RuleTimedOut _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a pathological pattern on an optional field times out to absent, and the rest of the extraction is unaffected`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = RegexCapture pathologicalPattern; Hint = AsDate "d MMM yyyy" } ]
    // "Ref: OK-1." ends with a sentence terminator, and longNonMatchingText starts lower-case -
    // without the period, TextNormalization's within-block join would treat the second line as
    // a wrapped continuation of the first and merge them into one line.
    let scanned = bodyMessage "" [ line 0 "Ref: OK-1."; line 0 longNonMatchingText; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice ->
        Assert.Equal("OK-1.", invoice.Reference)
        Assert.Equal(100.00m, invoice.Amount)
        Assert.Equal(Some "AUD", invoice.Currency)
        Assert.Equal(None, invoice.IssueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
