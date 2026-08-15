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

/// applyTemplate takes a NormalizedMessage, not a ScannedMessage - the type is what guarantees
/// the normalization happened, and happened once, above whatever loop is trying templates.
let private apply template scanned =
    ApplyTemplateWorkflow.applyTemplate zeroPaymentTerm testTemplateId template (MessageNormalization.normalizeMessage scanned)

/// CLAUDE.md -> Testing in this codebase -> Unit: the Ok path asserts EVERY output field, not
/// only the one a test is named for. Every rule-kind test below varies the Reference rule alone
/// and leaves the stable amount and currency rules in place, so the other seven fields have one
/// expected value between them - asserted here rather than restated eight times per test.
let private assertWholeInvoice (expectedReference: string) (actual: Result<ExtractedInvoice, InvoiceError>) =
    match actual with
    | Ok invoice ->
        Assert.Equal(expectedReference, invoice.Reference)
        Assert.Equal(100.00m, invoice.Amount)
        Assert.Equal("AUD", invoice.Currency)
        Assert.Equal(zeroTermSupplierId, SupplierId.value invoice.SupplierId)
        Assert.Equal("T1", TemplateId.value invoice.TemplateId)
        Assert.Equal("msg-1", SourceMessageId.value invoice.SourceMessageId)
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(None, invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``ApplyTemplateWorkflow.applyTemplate takes no dependency parameters`` () =
    // The signature itself is the assertion: PaymentTermDays -> TemplateId -> ValidTemplate ->
    // ScannedMessage -> Result<...>, nothing else - no getter, no clock, no network. TemplateId
    // is plain input data, not a dependency function type, so it does not violate that; it is
    // here because ExtractedInvoice.TemplateId and InvoiceError's template-carrying cases need
    // one and ValidTemplate itself carries none - a gap in design.md's listed signature.
    let template = validTemplate [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]

    let actual = apply template (bodyMessage "" [ stableAmountLine ])

    match actual with
    | Ok invoice ->
        Assert.Equal("X", invoice.Reference)
        Assert.Equal(100.00m, invoice.Amount)
        Assert.Equal("AUD", invoice.Currency)
        Assert.Equal(zeroTermSupplierId, SupplierId.value invoice.SupplierId)
        Assert.Equal("T1", TemplateId.value invoice.TemplateId)
        Assert.Equal("msg-1", SourceMessageId.value invoice.SourceMessageId)
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(None, invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

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
        Assert.Equal("AUD", invoice.Currency)
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

    apply template scanned |> assertWholeInvoice "INV-2099"

[<Fact; Trait("Level", "Unit")>]
let ``RegexCapture returns the first capture group of the first match`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = RegexCapture @"INV-(\d+)"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Reference is INV-3344 for your records"; stableAmountLine ]

    apply template scanned |> assertWholeInvoice "3344"

[<Fact; Trait("Level", "Unit")>]
let ``FixedValue returns that value without consulting the text at all`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "N/A"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ stableAmountLine ] // no reference-shaped text anywhere

    apply template scanned |> assertWholeInvoice "N/A"

[<Fact; Trait("Level", "Unit")>]
let ``SubjectCapture runs against the message subject, not the document body`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = SubjectCapture @"Ref (\w+)"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "Ref XY99 attached" [ stableAmountLine ]

    apply template scanned |> assertWholeInvoice "XY99"

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

    apply template scanned |> assertWholeInvoice "778899"

[<Fact; Trait("Level", "Unit")>]
let ``a label appearing twice takes the first occurrence`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Ref: FIRST"; line 0 "Ref: SECOND"; stableAmountLine ]

    apply template scanned |> assertWholeInvoice "FIRST"

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

    let actual =
        ApplyTemplateWorkflow.applyTemplate thirtyDayTerm testTemplateId template (MessageNormalization.normalizeMessage scanned)

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

    let normalized = MessageNormalization.normalizeMessage scanned
    let actualA = ApplyTemplateWorkflow.applyTemplate thirtyDayTerm testTemplateId templateA normalized
    let actualB = ApplyTemplateWorkflow.applyTemplate thirtyDayTerm testTemplateId templateB normalized

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
let ``a pathological pattern on an OPTIONAL field is still reported as RuleTimedOut, not swallowed as an absent value`` () =
    // requirements.md -> Regex safety states two separate things: fail THAT rule with
    // RuleTimedOut naming the field and the template, and allow the rest of the scan to finish.
    // "The field is simply absent" satisfies neither - a user whose IssueDate pattern
    // backtracks catastrophically would get no due date and nothing at all to diagnose from,
    // which is the exact silence the 12% -> 39% derivation exists to prevent.
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
    | Error (RuleTimedOut (templateId, field)) ->
        Assert.Equal<TargetField>(IssueDate, field)
        Assert.Equal("T1", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(RuleTimedOut _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``an optional field whose rule simply finds nothing is still absent rather than an error`` () =
    // The other half of the same rule, pinned so the fix above cannot over-reach: NotFound and
    // TimedOut are different outcomes and only one of them is an error.
    let template =
        validTemplate
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" } ]
    let scanned = bodyMessage "" [ line 0 "Ref: OK-1."; stableAmountLine ] // no "Date:" label at all

    let actual = apply template scanned

    match actual with
    | Ok invoice ->
        Assert.Equal("OK-1.", invoice.Reference)
        Assert.Equal(100.00m, invoice.Amount)
        Assert.Equal("AUD", invoice.Currency)
        Assert.Equal(None, invoice.IssueDate)
        Assert.Equal(None, invoice.DueDate)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``a rule that times out stops there rather than spending the whole timeout again on every later line`` () =
    // runRegexAcross promises in its own doc comment to stop at the first TimedOut. Twenty
    // candidate lines at 250ms of budget each is ~5s if the promise is not kept and ~250ms if
    // it is; selectTemplate then multiplies whichever it gets by the number of candidate
    // templates. requirements.md: "WHEN a rule times out THE SYSTEM SHALL NOT block the user
    // interface."
    let template =
        validTemplate
            [ { Field = Reference; Rule = RegexCapture pathologicalPattern; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    // Each line ends in '!' - a sentence terminator - so TextNormalization's within-block join
    // leaves twenty separate candidates rather than merging them into one long line.
    let scanned = bodyMessage "" (List.replicate 20 (line 0 longNonMatchingText) @ [ stableAmountLine ])
    let stopwatch = Diagnostics.Stopwatch.StartNew()

    let actual = apply template scanned

    stopwatch.Stop()

    match actual with
    | Error (RuleTimedOut (_, field)) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(RuleTimedOut _), but got {other}")

    Assert.True(
        stopwatch.ElapsedMilliseconds < 2000L,
        $"Took {stopwatch.ElapsedMilliseconds}ms - the pattern was evaluated against later lines instead of stopping at the first timeout")

// PR #11 review, finding 1: the amount must be ONE number read out of the line, never every
// digit on the line concatenated together.

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Total for INV-1042: $245.00")>]
[<InlineData("Total 245.00 due 14/07/2026")>]
[<InlineData("Total Ref 2 items $10.50")>]
let ``AsMoney refuses a line carrying more than one number rather than fusing them into a plausible wrong amount``
    (rawLine: string)
    =
    // AfterLabel "Total" without the colon, which is the shape the first line here needs and the
    // one the finding was reported against. Measured on the old character-filter implementation,
    // the remainders these leave booked -1042245.00, 245.0014072026 and 210.50 respectively -
    // silently, with no error to notice.
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Total"; Hint = AsMoney '.' }
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 rawLine ]

    let actual = apply template scanned

    match actual with
    | Error (AmountUnparseable (field, _)) -> Assert.Equal<TargetField>(Amount, field)
    | other -> Assert.Fail($"Expected Error(AmountUnparseable _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``AsMoney reads an amount that ends the sentence, trailing full stop and all`` () =
    // The flip side of the finding: tightening the scan must not start rejecting ordinary text.
    // The old filter turned "$245.00." into "245.00." and failed to parse it at all.
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Total: $245.00." ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal(245.00m, invoice.Amount)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

// PR #11 review, finding 3: LinesAfterLabel indexes within one block of one part, never across
// a boundary of either.

[<Fact; Trait("Level", "Unit")>]
let ``LinesAfterLabel does not step across a block boundary into the next block`` () =
    // requirements.md: "WHEN LinesAfterLabel is given an offset that runs past the end of the
    // block THE SYSTEM SHALL report that the rule found nothing". BlockIndex exists precisely
    // because "LinesAfterLabel depends on the structure a block boundary marks" - a label on the
    // last line of a table cell must not read the first line of the next one.
    let template =
        validTemplate
            [ { Field = Reference; Rule = LinesAfterLabel("Invoice number", 1); Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned =
        bodyMessage "" [ line 0 "Invoice number"; line 1 "NOT-THE-REFERENCE"; line 1 "Total: 100.00" ]

    let actual = apply template scanned

    match actual with
    | Error (TemplateMatchedNothing (_, field)) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``LinesAfterLabel does not step from the end of one attachment into the next`` () =
    // selectContent's own doc comment: a join must "never happen ACROSS two different parts -
    // they are different documents, and their BlockIndex numbering is unrelated". The offset has
    // to respect the same boundary, or a label ending cover-note.pdf reads the first line of the
    // next attachment. Both lines here sit in block 0, so only the PART split can catch this.
    let template =
        validTemplate
            [ { Field = Reference; Rule = LinesAfterLabel("Reference", 1); Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned =
        message
            ""
            [ AttachmentPart("cover-note.pdf", Pdf), [ line 0 "Reference" ]
              AttachmentPart("second.pdf", Pdf), [ line 0 "FROM-THE-NEXT-ATTACHMENT"; stableAmountLine ] ]

    let actual = apply template scanned

    match actual with
    | Error (TemplateMatchedNothing (_, field)) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

// PR #11 review, finding 4: requirements.md - "WHEN a rule finds nothing THE SYSTEM SHALL report
// which field and which rule found nothing, never a default or an empty value silently
// substituted." An empty extraction is a rule finding nothing.

[<Fact; Trait("Level", "Unit")>]
let ``AfterLabel on a label-only line reports the rule found nothing rather than an empty reference`` () =
    // The bare "Reference" line of the water-utility layout - the very shape LinesAfterLabel
    // exists for. AfterLabel on it leaves nothing after the label at all.
    let template =
        validTemplate
            [ { Field = Reference; Rule = AfterLabel "Reference"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Reference"; line 0 "WU-88213"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Error (TemplateMatchedNothing (_, field)) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a regex that matches but whose capture group did not participate reports the rule found nothing`` () =
    // (\d+)? can match while contributing nothing, so Groups.[1].Value is "" on a SUCCESSFUL
    // match - an empty reference that would collide in change #4's natural key, turning every
    // such invoice into the same ledger row.
    let template =
        validTemplate
            [ { Field = Reference; Rule = RegexCapture @"INV(\d+)?"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "INV follows on the next page"; stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Error (TemplateMatchedNothing (_, field)) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``FixedValue of an empty string reports the rule found nothing rather than storing an empty reference`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue ""; Hint = AsText }; stableAmountRule; stableCurrencyRule ]
    let scanned = bodyMessage "" [ stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Error (TemplateMatchedNothing (_, field)) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a whitespace-only currency is refused rather than stored as a blank currency`` () =
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              { Field = Currency; Rule = FixedValue "   "; Hint = AsText } ]
    let scanned = bodyMessage "" [ stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Error (TemplateMatchedNothing (_, field)) -> Assert.Equal<TargetField>(Currency, field)
    | other -> Assert.Fail($"Expected Error(TemplateMatchedNothing _), but got {other}")

// PR #11 review, finding 8: requirements.md - "WHEN ANY rule is evaluated THE SYSTEM SHALL first
// apply a defined normalization to the text". The subject and the attachment filenames are text
// a rule is evaluated against, so they go through the same normalization the body lines do.

[<Fact; Trait("Level", "Unit")>]
let ``SubjectCapture matches a subject carrying a non-breaking space, as the authoring panel showed it`` () =
    // Mailers insert U+00A0 around numbers, so a subject is one of the likeliest places to carry
    // one. The test panel displays the NORMALIZED text, so a pattern authored against what the
    // panel showed would otherwise match there and silently match nothing at scan time.
    let template =
        validTemplate
            [ { Field = Reference; Rule = SubjectCapture @"invoice (\S+) is attached"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    // The gap between "invoice" and the reference is U+00A0, not U+0020, so the pattern’s
    // plain space cannot match it until NFKC has folded it. Written as an escape because a
    // raw non-breaking space in a source file is invisible to the next reader.
    let scanned = bodyMessage "Your invoice\u00A0REF-66201 is attached" [ stableAmountLine ]

    apply template scanned |> assertWholeInvoice "REF-66201"

[<Fact; Trait("Level", "Unit")>]
let ``AttachmentName matches a filename whose digits arrive in full-width form`` () =
    // NFKC folds U+FF10..U+FF19 to plain ASCII digits. Without it \d still MATCHES the
    // full-width digits, so the rule appears to work and quietly stores a reference made of
    // characters no other source of the same reference will ever produce.
    let template =
        validTemplate
            [ { Field = Reference; Rule = AttachmentName @"^(\d+)\.pdf$"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned =
        message
            ""
            [ BodyPart, [ stableAmountLine ]
              AttachmentPart("９０３４５２１.pdf", Pdf), [] ]

    apply template scanned |> assertWholeInvoice "9034521"

// PR #11 review round 2, finding 1: a '-' is a sign only where a sign can legitimately appear.
// The round-1 rewrite fixed every line carrying TWO numbers; a hyphenated reference that is the
// only number on the line still passed the "exactly one candidate" guard and booked itself as a
// negative amount.

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Total items INV-1042")>]
[<InlineData("Total terms Net-30")>]
[<InlineData("Total order PO-77")>]
let ``AsMoney refuses a hyphenated reference rather than booking the digits after the hyphen as a negative amount``
    (rawLine: string)
    =
    // Measured on the round-1 implementation: these booked -1042, -30 and -77 respectively,
    // silently, because numericRuns treated the '-' inside INV-1042 as the run's sign. A label
    // collision - an Amount rule whose label also appears on a reference line - is all it takes.
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Total"; Hint = AsMoney '.' }
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 rawLine ]

    let actual = apply template scanned

    match actual with
    | Error (AmountUnparseable (field, _)) -> Assert.Equal<TargetField>(Amount, field)
    | other -> Assert.Fail($"Expected Error(AmountUnparseable _), but got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Total: -245.00", -245.00)>]
[<InlineData("Total: $-245.00", -245.00)>]
[<InlineData("Total: -1,234.56", -1234.56)>]
let ``AsMoney still reads a credit note, whose minus sign opens the number rather than following a letter``
    (rawLine: string, expected: float)
    =
    // The other half of the finding: the shape alone cannot tell -1042 in "INV-1042" from a
    // genuine credit note, but the CONTEXT can - a sign at the start of the text, after
    // whitespace or after a currency symbol is a sign; one following a letter or a digit is a
    // joiner inside a reference or a date. This pins the half that must keep working.
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

// PR #11 review round 2, finding 2: LinesAfterLabel counts the lines the document laid out, not
// the lines TextNormalization's within-block continuation join left behind.

[<Fact; Trait("Level", "Unit")>]
let ``LinesAfterLabel finds a value that starts lower-case, which the continuation join folds into its label line`` () =
    // "wu-88213" starts lower-case and "Reference" ends in no sentence terminator, so the join
    // treats the value as a wrapped continuation and merges the two lines. Counting the merged
    // lines then returns the line AFTER the value - "Total: 100.00" - as the reference.
    let template =
        validTemplate
            [ { Field = Reference; Rule = LinesAfterLabel("Reference", 1); Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Reference"; line 0 "wu-88213"; stableAmountLine ]

    apply template scanned |> assertWholeInvoice "wu-88213"

[<Theory; Trait("Level", "Unit")>]
[<InlineData("WU-88213")>]
[<InlineData("wu-88213")>]
let ``LinesAfterLabel returns the same line whether or not the value happens to start upper-case`` (reference: string) =
    // The defect stated as the property that matters: whether a template works must not depend on
    // the case of the first character of a value the template author does not control. The same
    // supplier's documents carry references in both cases.
    let template =
        validTemplate
            [ { Field = Reference; Rule = LinesAfterLabel("Reference", 1); Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Reference"; line 0 reference; stableAmountLine ]

    apply template scanned |> assertWholeInvoice reference

[<Fact; Trait("Level", "Unit")>]
let ``AfterLabel still reads a label hard-wrapped across two lines, which is what the join is for`` () =
    // requirements.md: "WHEN a label hard-wrapped across two lines is matched THE SYSTEM SHALL
    // find the value." The join stays, and every rule but LinesAfterLabel still reads its output -
    // fixing the offset must not cost this.
    let template =
        validTemplate
            [ { Field = Reference; Rule = AfterLabel "Invoice reference:"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Invoice"; line 0 "reference: INV-77"; stableAmountLine ]

    apply template scanned |> assertWholeInvoice "INV-77"

[<Fact; Trait("Level", "Unit")>]
let ``LinesAfterLabel counts from the laid-out line a hard-wrapped label ENDS on`` () =
    // The two halves of the fix meeting: the label is searched for in the JOINED text, so
    // "Invoice" / "reference:" is one line carrying "Invoice reference" and the label is found;
    // the offset is then counted over the LAID-OUT lines from the one the label finishes on, so
    // offset 1 is the line after "reference:" rather than the line after "Invoice". Counting from
    // where the label starts would return "reference:" itself.
    let template =
        validTemplate
            [ { Field = Reference; Rule = LinesAfterLabel("Invoice reference", 1); Hint = AsText }
              stableAmountRule
              stableCurrencyRule ]
    let scanned = bodyMessage "" [ line 0 "Invoice"; line 0 "reference:"; line 0 "INV-77"; stableAmountLine ]

    apply template scanned |> assertWholeInvoice "INV-77"

// PR #11 review round 2, finding 5: no exception leaves the domain, including from the one branch
// documented as pure arithmetic that never fails.

[<Fact; Trait("Level", "Unit")>]
let ``a derived due date that would fall outside DateTime's range is reported, not raised`` () =
    // DateTime.AddDays raises ArgumentOutOfRangeException once the result passes DateTime.MaxValue,
    // and the DateFromField branch had no failure channel at all - so the exception unwound out of
    // a workflow whose signature promises a Result.
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" }
              { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ]
    let scanned = bodyMessage "" [ line 0 "Date: 31 Dec 9999"; stableAmountLine ]
    let thirtyDayTerm = PaymentTermDays.create 30 |> valueOrFail

    let actual =
        ApplyTemplateWorkflow.applyTemplate thirtyDayTerm testTemplateId template (MessageNormalization.normalizeMessage scanned)

    match actual with
    | Error (DueDateOutOfRange (templateId, issueDate, paymentTermDays)) ->
        Assert.Equal("T1", TemplateId.value templateId)
        Assert.Equal(DateTime(9999, 12, 31), issueDate)
        Assert.Equal(30, paymentTermDays)
    | other -> Assert.Fail($"Expected Error(DueDateOutOfRange _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a derived due date that lands exactly on DateTime's last representable day is still produced`` () =
    // The boundary from the other side, so the overflow guard cannot be written one day too eager.
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              stableCurrencyRule
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" }
              { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ]
    let scanned = bodyMessage "" [ line 0 "Date: 1 Dec 9999"; stableAmountLine ]
    let thirtyDayTerm = PaymentTermDays.create 30 |> valueOrFail

    let actual =
        ApplyTemplateWorkflow.applyTemplate thirtyDayTerm testTemplateId template (MessageNormalization.normalizeMessage scanned)

    match actual with
    | Ok invoice -> Assert.Equal(Some (DateTime(9999, 12, 31)), invoice.DueDate)
    | other -> Assert.Fail($"Expected Ok, but got {other}")

// PR #11 review round 2, finding 6: Currency was the one extracted field returned without
// normalization, and it is change #4's natural key.

[<Fact; Trait("Level", "Unit")>]
let ``an extracted currency arrives trimmed, so two spellings of it cannot split one ledger row`` () =
    // extractMoney and extractDate both trim on their way through a parse step; Currency has no
    // parse step and was handed back raw. " AUD " and "AUD" from a sibling template are different
    // strings in change #4's natural key - cheapest to close before there is stored data.
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              { Field = Currency; Rule = FixedValue "  AUD  "; Hint = AsText } ]
    let scanned = bodyMessage "" [ stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal("AUD", invoice.Currency)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``a currency captured by a pattern arrives trimmed as well, not only a fixed one`` () =
    // The trim belongs to every outcome rather than to the one call site that remembered it:
    // AfterLabel trimmed its own substring, so the two paths that did not - FixedValue and a
    // capture group - were the ones that stored the whitespace.
    let template =
        validTemplate
            [ { Field = Reference; Rule = FixedValue "X"; Hint = AsText }
              stableAmountRule
              { Field = Currency; Rule = SubjectCapture @"\((.+)\)"; Hint = AsText } ]
    let scanned = bodyMessage "Invoice ( AUD )" [ stableAmountLine ]

    let actual = apply template scanned

    match actual with
    | Ok invoice -> Assert.Equal("AUD", invoice.Currency)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
