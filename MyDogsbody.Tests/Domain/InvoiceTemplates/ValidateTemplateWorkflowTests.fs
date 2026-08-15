module MyDogsbody.Tests.Domain.InvoiceTemplates.ValidateTemplateWorkflowTests

open System
open System.Globalization
open System.Text.RegularExpressions
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

/// Runs body with the ambient culture pinned, restoring it afterwards. Both of the culture rules
/// this file pins are invisible from the default culture: Regex captures its case-folding table at
/// construction time, and DateTime formatting resolves the calendar against CurrentCulture - so a
/// regression in either shows up only from inside one of these.
let private withCulture (name: string) (body: unit -> unit) =
    let original = CultureInfo.CurrentCulture

    try
        CultureInfo.CurrentCulture <- CultureInfo name
        body ()
    finally
        CultureInfo.CurrentCulture <- original

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
[<InlineData("Currency")>]
let ``validateTemplate refuses a template missing a required field with RequiredFieldHasNoRule naming it`` (missingFieldName: string) =
    let allRules = [ validReferenceRule; validAmountRule; validCurrencyRule ]
    let missingField =
        match missingFieldName with
        | "Reference" -> Reference
        | "Amount" -> Amount
        | "Currency" -> Currency
        | other -> failwith $"unexpected test field {other}"

    let input = { minimalValidTemplate with Rules = allRules |> List.filter (fun r -> r.Field <> missingField) }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (RequiredFieldHasNoRule field) -> Assert.Equal<TargetField>(missingField, field)
    | other -> Assert.Fail($"Expected Error(RequiredFieldHasNoRule _), but got {other}")

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

// PR #11 review, finding 9: the engine derives exactly one thing - DueDate from IssueDate. Any
// other DateFromField pairing used to pass validation and then yield None on every message
// forever, with no error, no field named, and nothing to diagnose from.

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses deriving a due date from a source the engine cannot derive from`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsDate "d MMM yyyy" } // date-hinted, so the existing checks pass
                  validAmountRule
                  validCurrencyRule
                  { Field = DueDate; Rule = DateFromField Reference; Hint = AsDate "d MMM yyyy" } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DerivationUnsupported (field, source)) ->
        Assert.Equal<TargetField>(DueDate, field)
        Assert.Equal<TargetField>(Reference, source)
    | other -> Assert.Fail($"Expected Error(DerivationUnsupported _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses deriving an issue date from a due date`` () =
    // applyTemplate evaluates IssueDate before DueDate, so this direction could never work -
    // it returned Ok None on line 231 rather than saying so.
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = DueDate; Rule = AfterLabel "Due:"; Hint = AsDate "d MMM yyyy" }
                  { Field = IssueDate; Rule = DateFromField DueDate; Hint = AsDate "d MMM yyyy" } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DerivationUnsupported (field, source)) ->
        Assert.Equal<TargetField>(IssueDate, field)
        Assert.Equal<TargetField>(DueDate, source)
    | other -> Assert.Fail($"Expected Error(DerivationUnsupported _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate still accepts the one derivation the engine supports`` () =
    // The regression lock on the two refusals above: DueDate from IssueDate is the 12% -> 39%
    // rule and must keep saving.
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" }
                  { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Ok template -> Assert.Equal(5, (ValidTemplate.rules template).Length)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

// PR #11 review, finding 10: a hint the engine cannot use for that field is a template that
// looks correct and can never work. requirements.md - "WHEN a user saves a template THE SYSTEM
// SHALL validate it AT THAT MOMENT, not when a scan next runs."

[<Theory; Trait("Level", "Unit")>]
[<InlineData("IssueDate")>]
[<InlineData("DueDate")>]
let ``validateTemplate refuses a date field whose rule reads text under a non-date hint`` (fieldName: string) =
    // Without this, extractDate defaulted the format to "" and every message failed the WHOLE
    // extraction with DateUnparseable(field, raw, "") - an error quoting an empty format string.
    let field = if fieldName = "IssueDate" then IssueDate else DueDate
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = field; Rule = AfterLabel "Date:"; Hint = AsText } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (FieldHintMismatch (reportedField, hint)) ->
        Assert.Equal<TargetField>(field, reportedField)
        Assert.Equal<ParseHint>(AsText, hint)
    | other -> Assert.Fail($"Expected Error(FieldHintMismatch _), but got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("AsText")>]
[<InlineData("AsDate")>]
let ``validateTemplate refuses an amount field carrying a hint that is not AsMoney`` (hintName: string) =
    // The mirror image: extractMoney silently defaulted the decimal separator to '.', so an
    // Amount rule hinted AsText parsed money anyway rather than being refused.
    let hint = if hintName = "AsText" then AsText else AsDate "d MMM yyyy"
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  { Field = Amount; Rule = AfterLabel "Total:"; Hint = hint }
                  validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (FieldHintMismatch (field, reportedHint)) ->
        Assert.Equal<TargetField>(Amount, field)
        Assert.Equal<ParseHint>(hint, reportedHint)
    | other -> Assert.Fail($"Expected Error(FieldHintMismatch _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate leaves a DateFromField rule's own hint alone`` () =
    // A derived DueDate never parses text, so its hint is not the engine's business - refusing
    // one here would reject every measured template that derives a due date.
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" }
                  { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsText } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Ok template -> Assert.Equal(5, (ValidTemplate.rules template).Length)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

// A null pattern is reachable: UnvalidatedTemplate is the untrusted type and TemplateName.create
// already guards isNull on the field beside it. Regex(null, ...) throws ArgumentNullException,
// which neither `with` clause in compilePattern names, so it would leave the domain as an
// exception rather than as a Result.

[<Fact; Trait("Level", "Unit")>]
let ``compilePattern refuses a null pattern with a reason rather than throwing`` () =
    let actual = ValidateTemplateWorkflow.compilePattern null

    match actual with
    | Error reason -> Assert.Equal("Pattern must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses a null pattern with PatternInvalid naming the field, rather than throwing`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ { Field = Reference; Rule = RegexCapture null; Hint = AsText }
                  validAmountRule
                  validCurrencyRule ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (PatternInvalid (field, reason)) ->
        Assert.Equal<TargetField>(Reference, field)
        Assert.Equal("Pattern must not be empty.", reason)
    | other -> Assert.Fail($"Expected Error(PatternInvalid _), but got {other}")

// RegexOptions.IgnoreCase alone folds case against CultureInfo.CurrentCulture, captured when the
// Regex is constructed. Under tr-TR / az-AZ the dotless i makes 'I' fold to 'ı' rather than 'i',
// so a stored template that matches on one machine silently matches nothing on another - no
// error, no refusal. CultureInvariant pins the folding table; these two tests are the only thing
// in the suite that would catch its removal.

[<Fact; Trait("Level", "Unit")>]
let ``compilePattern folds case invariantly, so a pattern still matches under a culture with different case rules`` () =
    withCulture "tr-TR" (fun () ->
        match ValidateTemplateWorkflow.compilePattern @"INVOICE (\S+)" with
        | Ok regex ->
            Assert.True(regex.Options.HasFlag RegexOptions.CultureInvariant)
            Assert.True(regex.IsMatch "invoice INV-42", "Turkish-I case folding broke a case-insensitive match")
        | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}"))

[<Fact; Trait("Level", "Unit")>]
let ``compilePattern's backtracking fallback folds case invariantly too`` () =
    withCulture "tr-TR" (fun () ->
        // A lookahead forces the fallback engine - the fix has to reach both constructions.
        match ValidateTemplateWorkflow.compilePattern @"INVOICE (?=INV)(\S+)" with
        | Ok regex ->
            Assert.False(regex.Options.HasFlag RegexOptions.NonBacktracking)
            Assert.True(regex.Options.HasFlag RegexOptions.CultureInvariant)
            Assert.True(regex.IsMatch "invoice INV-42", "Turkish-I case folding broke the fallback engine's match")
        | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}"))

// A date format is validated with the operation it will be used for - parsing - not merely with
// formatting. DateTime.ToString rejects only a lone unknown standard specifier or an unterminated
// quote; every other unrecognised character is emitted as a literal, so a format-only check
// accepts "yyyy-MM-DD" and "dd/mm/yyyy" (mm is minutes) and each then silently matches nothing at
// scan time - the failure mode tasks.md calls the most dangerous one.

[<Theory; Trait("Level", "Unit")>]
[<InlineData("yyyy-MM-DD")>] // DD is a literal, so the day never parses back
[<InlineData("YYYY-MM-DD")>] // YYYY is a literal too
[<InlineData("dd/mm/yyyy")>] // mm is MINUTES - the most realistic typo of the set
[<InlineData("qq")>] // no specifier at all
[<InlineData("HH:mm")>] // formats and parses, but carries no date
[<InlineData("")>] // renders under the general format, cannot be parsed back
let ``validateTemplate refuses a date format that cannot read back the date it writes`` (format: string) =
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
        Assert.Contains(format, reason)
    | other -> Assert.Fail($"Expected Error(DateFormatInvalid _) for '{format}', but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate refuses a null date format with DateFormatInvalid naming the field`` () =
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate null } ] }

    let actual = ValidateTemplateWorkflow.validateTemplate input

    match actual with
    | Error (DateFormatInvalid (field, reason)) ->
        Assert.Equal<TargetField>(IssueDate, field)
        Assert.False(String.IsNullOrWhiteSpace reason)
    | other -> Assert.Fail($"Expected Error(DateFormatInvalid _), but got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("yyyy-MM-dd")>]
[<InlineData("dd/MM/yyyy")>]
[<InlineData("d MMM yyyy")>]
[<InlineData("dddd, d MMMM yyyy")>]
[<InlineData("yyyyMMdd")>]
[<InlineData("dd-MM-yy")>]
[<InlineData("yyyy-MM-dd HH:mm")>]
[<InlineData("d")>] // a standard specifier, not a custom one
let ``validateTemplate accepts a date format that round-trips a whole calendar date`` (format: string) =
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate format } ] }

    match ValidateTemplateWorkflow.validateTemplate input with
    | Ok _ -> ()
    | Error error -> Assert.Fail($"Expected Ok for '{format}', but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``validateTemplate judges a date format identically under any ambient culture`` () =
    // The probe renders as 2569-03-04 under th-TH and 1447-09-15 under ar-SA if the calendar is
    // taken from CurrentCulture, so a template's acceptance would depend on the machine that saved
    // it. Pinning InvariantCulture at both ends of the check is what this asserts - and the fixed
    // probe date it needs is also why the domain no longer reads DateTime.Now here.
    let input =
        { minimalValidTemplate with
            Rules =
                [ validReferenceRule
                  validAmountRule
                  validCurrencyRule
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "yyyy-MM-dd" } ] }

    for culture in [ "en-AU"; "th-TH"; "ar-SA"; "tr-TR" ] do
        withCulture culture (fun () ->
            match ValidateTemplateWorkflow.validateTemplate input with
            | Ok _ -> ()
            | Error error -> Assert.Fail($"Expected Ok under {culture}, but got Error: {error}"))
