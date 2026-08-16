module MyDogsbody.Tests.Domain.InvoiceTemplates.ValidateTemplateWorkflowTests

open System
open System.Text.RegularExpressions
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

// compilePattern: NonBacktracking first, backtracking-with-timeout as fallback. Both branches
// carry a 250ms match timeout - the timeout is the availability guarantee, NonBacktracking is
// only the cheap way to make it unnecessary for most patterns. Whether a result used the
// fallback engine is read off the returned Regex's own Options, not a separate flag - the Regex
// object already carries that fact.

[<Fact; Trait("Level", "Unit")>]
let ``compilePattern compiles a plain pattern using the NonBacktracking engine`` () =
    let actual = ValidateTemplateWorkflow.compilePattern "Total: (.+)"

    match actual with
    | Ok regex -> Assert.True(regex.Options.HasFlag RegexOptions.NonBacktracking)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``compilePattern falls back to the backtracking engine for a pattern NonBacktracking rejects, and reports that it did`` () =
    // A lookahead is one of the constructs RegexOptions.NonBacktracking does not support -
    // verified empirically: constructing it throws NotSupportedException, not ArgumentException.
    let actual = ValidateTemplateWorkflow.compilePattern "foo(?=bar)"

    match actual with
    | Ok regex ->
        Assert.False(regex.Options.HasFlag RegexOptions.NonBacktracking)
        Assert.Equal(TimeSpan.FromMilliseconds 250.0, regex.MatchTimeout)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``compilePattern returns an error with the reason for a syntactically invalid pattern`` () =
    let actual = ValidateTemplateWorkflow.compilePattern "(unbalanced"

    match actual with
    | Error reason -> Assert.Contains("Not enough )", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``a catastrophic-backtracking pattern against a long non-matching input completes inside the timeout rather than hanging`` () =
    // (a+)+$ is the textbook catastrophic-backtracking pattern. It compiles on the
    // NonBacktracking path (verified empirically - nested quantifiers are exactly what that
    // engine exists to handle without exponential blowup), so this proves the pattern is safe to
    // match regardless of which engine ends up matching it: either NonBacktracking's own
    // algorithmic guarantee, or the 250ms timeout, but never an unbounded hang.
    let compiled = ValidateTemplateWorkflow.compilePattern "(a+)+$"

    match compiled with
    | Ok regex ->
        let longNonMatchingInput = String('a', 40) + "!"
        let stopwatch = Diagnostics.Stopwatch.StartNew()

        let completed =
            try
                regex.IsMatch longNonMatchingInput |> ignore
                true
            with :? RegexMatchTimeoutException ->
                true

        stopwatch.Stop()
        Assert.True(completed)
        Assert.True(stopwatch.ElapsedMilliseconds < 2000L, $"Took {stopwatch.ElapsedMilliseconds}ms")
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

// validateTemplate: the only door to ValidTemplate. Every refusal below starts from a minimal
// template that is otherwise valid (Reference/Amount/Currency each covered, no dates, no
// patterns) and perturbs exactly the one thing the test names, so a failure it its own proof
// that the check under test is what actually fired.

let private validReferenceRule = { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
let private validAmountRule = { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
let private validCurrencyRule = { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }

let private validTemplate (rules: TemplateFieldRule list) : UnvalidatedTemplate =
    { SupplierId = "1"
      Name = "Monthly statement"
      Part = AnyPart
      Position = 0
      Rules = rules }

let private minimalValidTemplate =
    validTemplate [ validReferenceRule; validAmountRule; validCurrencyRule ]

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate accepts a minimal valid template and reports every field on the result`` () =
    let withCapture =
        { minimalValidTemplate with
            Rules =
                [ { Field = Reference; Rule = RegexCapture @"INV-(\d+)"; Hint = AsText }
                  validAmountRule
                  validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate withCapture

    match actual with
    | Ok template ->
        Assert.Equal("1", SupplierId.value (ValidTemplate.supplierId template))
        Assert.Equal("Monthly statement", TemplateName.value (ValidTemplate.name template))
        Assert.Equal<DocumentPart>(AnyPart, ValidTemplate.part template)
        Assert.Equal(0, ValidTemplate.position template)
        Assert.Equal(3, (ValidTemplate.rules template).Length)
        let compiled = ValidTemplate.compiledPatterns template
        Assert.True(Map.containsKey Reference compiled)
        Assert.True((Map.find Reference compiled).IsMatch "INV-1234"
                    && (Map.find Reference compiled).Match("INV-1234").Groups.[1].Value = "1234")
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses an empty name with TemplateNameInvalid carrying the reason`` () =
    let input = { minimalValidTemplate with Name = "" }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (TemplateNameInvalid reason) -> Assert.Equal("Template name must not be empty.", reason)
    | other -> Assert.Fail($"Expected Error(TemplateNameInvalid _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses an unparseable supplier id with TemplateSupplierIdInvalid carrying the reason`` () =
    let input = { minimalValidTemplate with SupplierId = "" }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (TemplateSupplierIdInvalid reason) -> Assert.Equal("Supplier id must not be empty.", reason)
    | other -> Assert.Fail($"Expected Error(TemplateSupplierIdInvalid _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses a pattern that does not compile with PatternInvalid naming the field and reason`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ { Field = Reference; Rule = RegexCapture "(unbalanced"; Hint = AsText }
                  validAmountRule
                  validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (PatternInvalid (field, reason)) ->
        Assert.Equal<TargetField>(Reference, field)
        Assert.Contains("Not enough )", reason)
    | other -> Assert.Fail($"Expected Error(PatternInvalid _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses a pattern with no capture group with PatternHasNoCaptureGroup naming the field`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ { Field = Reference; Rule = RegexCapture "INV-\\d+"; Hint = AsText }
                  validAmountRule
                  validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (PatternHasNoCaptureGroup field) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(PatternHasNoCaptureGroup _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses a date format that is not a real format string with DateFormatInvalid naming the field and reason`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "'unterminated" } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DateFormatInvalid (field, reason)) ->
        Assert.Equal<TargetField>(IssueDate, field)
        Assert.False(System.String.IsNullOrWhiteSpace reason)
    | other -> Assert.Fail($"Expected Error(DateFormatInvalid _), but got {other}")

// PR #14 review round 3: the format check was `DateTime.Now.ToString format` catching
// FormatException, and .NET treats an empty format string as the general "G" pattern rather
// than throwing - so `AsDate ""` validated, saved, and then failed on every single scan with
// DateUnparseable(..., format = ""), since TryParseExact rejects an empty format. It is
// reachable from the editor by choosing "AsDate" and leaving the date-format box blank, and the
// AsMoney arm of the same hint already refuses its missing separator.
[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``validateTemplate refuses a blank date format with DateFormatInvalid naming the field`` (format: string) =
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate format } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DateFormatInvalid (field, reason)) ->
        Assert.Equal<TargetField>(IssueDate, field)
        Assert.Contains("date format", reason)
    | other -> Assert.Fail($"Expected Error(DateFormatInvalid _), but got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(-1)>]
[<InlineData(21)>]
let ``validateTemplate refuses an out-of-range offset with OffsetOutOfRange naming the field and offset`` (offset: int) =
    let input =
        { minimalValidTemplate with
            Rules =
                [ { Field = Reference; Rule = LinesAfterLabel("Invoice:", offset); Hint = AsText }
                  validAmountRule
                  validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (OffsetOutOfRange (field, actualOffset)) ->
        Assert.Equal<TargetField>(Reference, field)
        Assert.Equal(offset, actualOffset)
    | other -> Assert.Fail($"Expected Error(OffsetOutOfRange _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate accepts an offset at each end of the 0 to 20 range`` () =
    for offset in [ 0; 20 ] do
        let input =
            { minimalValidTemplate with
                Rules =
                    [ { Field = Reference; Rule = LinesAfterLabel("Invoice:", offset); Hint = AsText }
                      validAmountRule
                      validCurrencyRule ] }

        match ValidateTemplateWorkflow.validateTemplate input with
        | Ok _ -> ()
        | Error error -> Assert.Fail($"Expected Ok for offset {offset}, but got Error: {error}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Reference")>]
[<InlineData("Amount")>]
let ``validateTemplate refuses a template missing a required field with RequiredFieldHasNoRule naming it`` (missingFieldName: string) =
    // Currency is not in this list - it is not required, and has its own test below asserting
    // exactly that.
    let allRules = [ validReferenceRule; validAmountRule; validCurrencyRule ]
    let missingField =
        match missingFieldName with
        | "Reference" -> Reference
        | "Amount" -> Amount
        | other -> failwith $"unexpected test field {other}"

    let input = { minimalValidTemplate with Rules = allRules |> List.filter (fun r -> r.Field <> missingField) }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (RequiredFieldHasNoRule field) -> Assert.Equal<TargetField>(missingField, field)
    | other -> Assert.Fail($"Expected Error(RequiredFieldHasNoRule _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate accepts a template with no Currency rule - Currency is not required`` () =
    let input =
        { minimalValidTemplate with
            Rules = [ validReferenceRule; validAmountRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Ok template -> Assert.Empty(ValidTemplate.rules template |> List.filter (fun r -> r.Field = Currency))
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses two rules targeting the same field with DuplicateRuleForField naming it`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = Amount; Rule = AfterLabel "Balance:"; Hint = AsMoney '.' } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DuplicateRuleForField field) -> Assert.Equal<TargetField>(Amount, field)
    | other -> Assert.Fail($"Expected Error(DuplicateRuleForField _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses a DateFromField whose source has no rule with DerivationSourceMissing naming the source`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DerivationSourceMissing source) -> Assert.Equal<TargetField>(IssueDate, source)
    | other -> Assert.Fail($"Expected Error(DerivationSourceMissing _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses a DateFromField naming a non-date source with DerivationSourceNotADate naming the source`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsText } // date field, but NOT date-hinted
                  { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DerivationSourceNotADate source) -> Assert.Equal<TargetField>(IssueDate, source)
    | other -> Assert.Fail($"Expected Error(DerivationSourceNotADate _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses a DateFromField naming its own field as the source with DerivationSourceIsSelf`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = DueDate; Rule = DateFromField DueDate; Hint = AsDate "d MMM yyyy" } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DerivationSourceIsSelf field) -> Assert.Equal<TargetField>(DueDate, field)
    | other -> Assert.Fail($"Expected Error(DerivationSourceIsSelf _), but got {other}")

// PR #14 review: a blank label / value passed validation, and a blank label matches EVERY
// document - String.IndexOf("") returns 0, so AfterLabel "" resolves against line 0 of anything
// and hands back that whole line as the value. The three pattern-carrying kinds were already
// covered by accident (a blank pattern compiles, but has no capture group, so
// PatternHasNoCaptureGroup rejects it); AfterLabel / LinesAfterLabel / FixedValue had nothing
// checking them at all.

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("\t")>]
let ``validateTemplate refuses an AfterLabel whose label is blank with RuleTextEmpty naming the field`` (label: string) =
    let input =
        { minimalValidTemplate with
            Rules = [ { Field = Reference; Rule = AfterLabel label; Hint = AsText }; validAmountRule; validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (RuleTextEmpty field) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(RuleTextEmpty Reference), but got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``validateTemplate refuses a LinesAfterLabel whose label is blank with RuleTextEmpty naming the field`` (label: string) =
    let input =
        { minimalValidTemplate with
            Rules = [ { Field = Reference; Rule = LinesAfterLabel(label, 1); Hint = AsText }; validAmountRule; validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (RuleTextEmpty field) -> Assert.Equal<TargetField>(Reference, field)
    | other -> Assert.Fail($"Expected Error(RuleTextEmpty Reference), but got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``validateTemplate refuses a FixedValue whose value is blank with RuleTextEmpty naming the field`` (value: string) =
    let input =
        { minimalValidTemplate with
            Rules = [ validReferenceRule; validAmountRule; { Field = Currency; Rule = FixedValue value; Hint = AsText } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (RuleTextEmpty field) -> Assert.Equal<TargetField>(Currency, field)
    | other -> Assert.Fail($"Expected Error(RuleTextEmpty Currency), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate still accepts a label that is merely short rather than blank`` () =
    // The guard is "blank", not "short" - a one-character label is legitimate.
    let input =
        { minimalValidTemplate with
            Rules = [ { Field = Reference; Rule = AfterLabel "#"; Hint = AsText }; validAmountRule; validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Ok template -> Assert.Equal(3, (ValidTemplate.rules template).Length)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
