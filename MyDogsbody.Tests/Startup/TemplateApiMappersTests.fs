module MyDogsbody.Tests.Startup.TemplateApiMappersTests

open System
open Xunit
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private mappedOrFail (result: Result<'T, TemplateError>) =
    match result with
    | Ok value -> value
    | Error error -> failwith $"Expected the mapping to succeed, but got: {error}"

let private anAction = ActionNames.MyDogsbody.Startup.TemplateApi.addTemplate

let private uiRule field ruleKind ruleText ruleOffset ruleSourceField hintKind hintText : TemplateFieldRuleUiType =
    { Field = field; RuleKind = ruleKind; RuleText = ruleText; RuleOffset = ruleOffset; RuleSourceField = ruleSourceField; HintKind = hintKind; HintText = hintText }

let private validRulesUi =
    [ uiRule "Reference" "AfterLabel" "Invoice:" 0 "" "AsText" ""
      uiRule "Amount" "AfterLabel" "Total:" 0 "" "AsMoney" "."
      uiRule "Currency" "FixedValue" "AUD" 0 "" "AsText" "" ]

// ---------- domain <-> UI record ----------

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedTemplate carries every field of the UI record`` () =
    let entered: TemplateUiTypeWithoutId =
        { SupplierId = "1"; Name = "Monthly statement"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 2; Rules = validRulesUi }

    let actual = TemplateApiMappers.toUnvalidatedTemplate entered |> mappedOrFail

    Assert.Equal("1", actual.SupplierId)
    Assert.Equal("Monthly statement", actual.Name)
    Assert.Equal<DocumentPart>(AnyPart, actual.Part)
    Assert.Equal(2, actual.Position)
    Assert.Equal<TargetField list>([ Reference; Amount; Currency ], actual.Rules |> List.map (fun r -> r.Field))
    Assert.Equal(AfterLabel "Invoice:", (actual.Rules |> List.find (fun r -> r.Field = Reference)).Rule)
    Assert.Equal(AsMoney '.', (actual.Rules |> List.find (fun r -> r.Field = Amount)).Hint)

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedTemplate maps an Attachment document part carrying its format`` () =
    let entered: TemplateUiTypeWithoutId =
        { SupplierId = "1"; Name = "T"; DocumentPart = "Attachment"; AttachmentFormat = "Pdf"; Position = 0; Rules = validRulesUi }

    let actual = TemplateApiMappers.toUnvalidatedTemplate entered |> mappedOrFail

    Assert.Equal<DocumentPart>(Attachment MyDogsbody.Domain.Documents.Pdf, actual.Part)

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedTemplate maps a DateFromField rule carrying its source field`` () =
    let entered: TemplateUiTypeWithoutId =
        { SupplierId = "1"
          Name = "T"
          DocumentPart = "AnyPart"
          AttachmentFormat = ""
          Position = 0
          Rules = validRulesUi @ [ uiRule "DueDate" "DateFromField" "" 0 "IssueDate" "AsDate" "d MMM yyyy" ] }

    let actual = TemplateApiMappers.toUnvalidatedTemplate entered |> mappedOrFail

    Assert.Equal(DateFromField IssueDate, (actual.Rules |> List.find (fun r -> r.Field = DueDate)).Rule)

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedTemplateEdit carries every field of the UI record including the id`` () =
    let entered: TemplateUiType =
        { Id = "7"; SupplierId = "1"; Name = "Monthly statement"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 2; Rules = validRulesUi }

    let id, actual = TemplateApiMappers.toUnvalidatedTemplateEdit entered |> mappedOrFail

    Assert.Equal("7", id)
    Assert.Equal("1", actual.SupplierId)
    Assert.Equal("Monthly statement", actual.Name)
    Assert.Equal<TargetField list>([ Reference; Amount; Currency ], actual.Rules |> List.map (fun r -> r.Field))

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedTemplate rejects an unrecognised rule kind as TemplateRuleShapeInvalid`` () =
    let entered: TemplateUiTypeWithoutId =
        { SupplierId = "1"
          Name = "T"
          DocumentPart = "AnyPart"
          AttachmentFormat = ""
          Position = 0
          Rules = [ uiRule "Reference" "Bogus" "x" 0 "" "AsText" "" ] }

    match TemplateApiMappers.toUnvalidatedTemplate entered with
    | Error (TemplateRuleShapeInvalid reason) -> Assert.Contains("Bogus", reason)
    | other -> Assert.Fail($"Expected Error(TemplateRuleShapeInvalid _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedTemplate rejects an unrecognised document part as TemplateRuleShapeInvalid`` () =
    let entered: TemplateUiTypeWithoutId =
        { SupplierId = "1"; Name = "T"; DocumentPart = "Bogus"; AttachmentFormat = ""; Position = 0; Rules = validRulesUi }

    match TemplateApiMappers.toUnvalidatedTemplate entered with
    | Error (TemplateRuleShapeInvalid reason) -> Assert.Contains("Bogus", reason)
    | other -> Assert.Fail($"Expected Error(TemplateRuleShapeInvalid _), but got {other}")

/// The file's own header rule: "Every string->union conversion below returns Result rather than
/// raising ... these mappers are called from Async.Start, where an uncaught exception reaches
/// neither an alert nor a log." A cleared MudTextField hands a bound `string` back as null, so an
/// AsMoney rule whose separator box was emptied is the ordinary way this arrives - and it is the
/// one field of the seven the mapper DEREFERENCES rather than matches, so it raised
/// NullReferenceException through AddTemplate, EditTemplate and TestTemplate alike.
[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedTemplate reports a missing AsMoney separator rather than raising`` () =
    let separatorsWithNoCharacter: string list = [ null; "" ]

    for separator in separatorsWithNoCharacter do
        let entered: TemplateUiTypeWithoutId =
            { SupplierId = "1"
              Name = "T"
              DocumentPart = "AnyPart"
              AttachmentFormat = ""
              Position = 0
              Rules = [ uiRule "Amount" "AfterLabel" "Total:" 0 "" "AsMoney" separator ] }

        match TemplateApiMappers.toUnvalidatedTemplate entered with
        | Error (TemplateRuleShapeInvalid reason) ->
            Assert.Equal("AsMoney hint is missing its decimal separator.", reason)
        | other -> Assert.Fail($"Expected Error(TemplateRuleShapeInvalid _) for {separator |> box}, but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``toUiType and toUnvalidatedTemplate round trip a stored template unchanged`` () =
    let unvalidated: UnvalidatedTemplate =
        {
            SupplierId = "1"
            Name = "Monthly statement"
            Part = AnyPart
            Position = 2
            Rules =
                [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
                  { Field = Amount; Rule = LinesAfterLabel("Total", 1); Hint = AsMoney ',' }
                  { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
                  { Field = IssueDate; Rule = RegexCapture @"Date: (\d+)"; Hint = AsDate "d MMM yyyy" }
                  { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ]
        }
    let validated =
        match ValidateTemplateWorkflow.validateTemplate unvalidated with
        | Ok template -> template
        | Error error -> failwith $"Test setup produced an invalid template: {error}"
    let stored: StoredTemplate = { Id = TemplateId.create "7" |> valueOrFail; Template = validated }

    let uiType = TemplateApiMappers.toUiType stored
    let roundTrippedId, roundTripped = TemplateApiMappers.toUnvalidatedTemplateEdit uiType |> mappedOrFail

    Assert.Equal("1", uiType.SupplierId)
    Assert.Equal("7", uiType.Id)
    Assert.Equal("Monthly statement", uiType.Name)
    Assert.Equal(2, uiType.Position)
    Assert.Equal("7", roundTrippedId)
    Assert.Equal<TargetField list>([ Reference; Amount; Currency; IssueDate; DueDate ], roundTripped.Rules |> List.map (fun r -> r.Field))
    Assert.Equal(LinesAfterLabel("Total", 1), (roundTripped.Rules |> List.find (fun r -> r.Field = Amount)).Rule)
    Assert.Equal(AsMoney ',', (roundTripped.Rules |> List.find (fun r -> r.Field = Amount)).Hint)
    Assert.Equal(DateFromField IssueDate, (roundTripped.Rules |> List.find (fun r -> r.Field = DueDate)).Rule)

// ---------- every union case, both directions ----------

/// Puts a rule set through validation - the only door to ValidTemplate, so the round trip starts
/// from a template the domain would actually have stored - then out through toUiType and back in
/// through toUnvalidatedTemplateEdit. Hands back both halves: the UI columns the domain became, and
/// the domain rebuilt from those columns.
let private roundTrip (part: DocumentPart) (rules: TemplateFieldRule list) : TemplateUiType * UnvalidatedTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = "1"; Name = "Monthly statement"; Part = part; Position = 4; Rules = rules }

    let validated =
        match ValidateTemplateWorkflow.validateTemplate unvalidated with
        | Ok template -> template
        | Error error -> failwith $"Test setup produced an invalid template: {error}"

    let uiType = TemplateApiMappers.toUiType { Id = TemplateId.create "7" |> valueOrFail; Template = validated }
    let _, back = TemplateApiMappers.toUnvalidatedTemplateEdit uiType |> mappedOrFail

    uiType, back

/// The two required rules that keep a template validatable while a third is under test.
let private requiredRulesBeside (rule: TemplateFieldRule) : TemplateFieldRule list =
    [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
      { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
      { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]
    |> List.filter (fun beside -> beside.Field <> rule.Field)

/// toFieldRuleUiColumns decides which kind string a stored rule is SHOWN and re-saved as, and the
/// editor edits what it is shown: EditTemplate maps the shown string straight back to a FieldRule,
/// so a kind swapped between two cases does not merely mislabel a row - the next save silently
/// rewrites what the rule reads. SubjectCapture reads the subject and AttachmentName the filename,
/// so that particular swap changes the answer on every message, forever, with nothing to notice it
/// by.
///
/// Measured before this test existed: swapping SubjectCapture's and AttachmentName's kind strings
/// passed the whole suite, 773 of 773. Only five of the seven kinds were round-tripped anywhere.
///
/// One row per FieldRule case, counted against the union by reflection, so an eighth kind fails
/// here rather than being round-tripped by nothing.
[<Fact; Trait("Level", "Contract")>]
let ``every FieldRule kind survives the round trip carrying its own UI columns`` () =
    // the rule under test, the rules it needs beside it, and the exact columns it must become
    let cases: (TemplateFieldRule * TemplateFieldRule list * (string * string * int * string)) list =
        [
            let onReference rule : TemplateFieldRule = { Field = Reference; Rule = rule; Hint = AsText }

            let plain rule columns =
                let underTest = onReference rule
                underTest, requiredRulesBeside underTest, columns

            plain (AfterLabel "Invoice:") ("AfterLabel", "Invoice:", 0, "")
            plain (LinesAfterLabel("Invoice", 3)) ("LinesAfterLabel", "Invoice", 3, "")
            plain (RegexCapture @"INV-(\d+)") ("RegexCapture", @"INV-(\d+)", 0, "")
            plain (FixedValue "REF-1") ("FixedValue", "REF-1", 0, "")
            plain (SubjectCapture @"INV-(\d+)") ("SubjectCapture", @"INV-(\d+)", 0, "")
            plain (AttachmentName @"(\d+)\.pdf") ("AttachmentName", @"(\d+)\.pdf", 0, "")

            // The one kind that cannot sit on Reference: validation admits only DueDate from
            // IssueDate, and the source needs an AsDate-hinted rule of its own.
            let derived: TemplateFieldRule = { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" }

            derived,
            requiredRulesBeside derived
            @ [ { Field = IssueDate; Rule = AfterLabel "Dated:"; Hint = AsDate "d MMM yyyy" } ],
            ("DateFromField", "", 0, "IssueDate")
        ]

    let declaredKinds = Reflection.FSharpType.GetUnionCases(typeof<FieldRule>) |> Array.length
    Assert.Equal(declaredKinds, List.length cases)

    for underTest, beside, (ruleKind, ruleText, ruleOffset, ruleSourceField) in cases do
        let uiType, back = roundTrip AnyPart (beside @ [ underTest ])
        let uiField = TemplateApiMappers.toTargetFieldUiString underTest.Field
        let column = uiType.Rules |> List.find (fun rule -> rule.Field = uiField)

        // Outbound: the exact columns, field for field.
        Assert.Equal(ruleKind, column.RuleKind)
        Assert.Equal(ruleText, column.RuleText)
        Assert.Equal(ruleOffset, column.RuleOffset)
        Assert.Equal(ruleSourceField, column.RuleSourceField)

        // Inbound: the same rule back out of those columns, payload and all.
        let rebuilt = back.Rules |> List.find (fun rule -> rule.Field = underTest.Field)
        Assert.Equal(underTest.Rule, rebuilt.Rule)
        Assert.Equal(underTest.Hint, rebuilt.Hint)

/// The same argument one level up: DocumentPart and its DocumentFormat decide which part of a
/// message the template is ever shown, and an edit re-saves whatever the columns said. A stored
/// Attachment(Word) template shown as "Pdf" is re-saved as Attachment(Pdf) and then matches no
/// message it used to match - silently, because a template that selects no part simply reports
/// every field as unmatched.
///
/// Measured before this test existed: mapping Word to "Pdf" in toDocumentFormatUiString passed the
/// whole suite, 773 of 773. No format was round-tripped in either mapper test, and only AnyPart was.
[<Fact; Trait("Level", "Contract")>]
let ``every DocumentPart and DocumentFormat survives the round trip carrying its own UI columns`` () =
    let cases: (DocumentPart * (string * string)) list =
        [
            Body, ("Body", "")
            AnyPart, ("AnyPart", "")
            Attachment Pdf, ("Attachment", "Pdf")
            Attachment Word, ("Attachment", "Word")
            Attachment PlainText, ("Attachment", "PlainText")
            Attachment EmailBody, ("Attachment", "EmailBody")
        ]

    // Every DocumentPart case, and every DocumentFormat an Attachment can carry.
    let declaredParts = Reflection.FSharpType.GetUnionCases(typeof<DocumentPart>) |> Array.length
    let declaredFormats = Reflection.FSharpType.GetUnionCases(typeof<DocumentFormat>) |> Array.length
    Assert.Equal(declaredParts, cases |> List.map (fun (_, (partColumn, _)) -> partColumn) |> List.distinct |> List.length)
    Assert.Equal(declaredFormats, cases |> List.filter (fun (_, (partColumn, _)) -> partColumn = "Attachment") |> List.length)

    for part, (partColumn, formatColumn) in cases do
        let rules =
            [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]

        let uiType, back = roundTrip part rules

        Assert.Equal(partColumn, uiType.DocumentPart)
        Assert.Equal(formatColumn, uiType.AttachmentFormat)
        Assert.Equal(part, back.Part)

/// tasks.md 8.1: "an unrecognised string per union returns its error case rather than raising".
/// The rule kind and the document part already had one each; the target field, the parse hint, the
/// attachment format and the DateFromField source field did not, and each is a separate `match` in
/// this file that a stored row or a bound control can miss.
[<Fact; Trait("Level", "Contract")>]
let ``an unrecognised string for every union the UI supplies is refused rather than raising`` () =
    let template rules : TemplateUiTypeWithoutId =
        { SupplierId = "1"; Name = "T"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = rules }

    let cases: (string * TemplateUiTypeWithoutId * string) list =
        [
            "TargetField",
            template [ uiRule "Bogus" "AfterLabel" "Invoice:" 0 "" "AsText" "" ],
            "Target field 'Bogus' has no domain equivalent."

            "ParseHint kind",
            template [ uiRule "Reference" "AfterLabel" "Invoice:" 0 "" "Bogus" "" ],
            "Hint kind 'Bogus' has no domain equivalent."

            "DateFromField source field",
            template [ uiRule "DueDate" "DateFromField" "" 0 "Bogus" "AsDate" "d MMM yyyy" ],
            "Target field 'Bogus' has no domain equivalent."

            "DocumentFormat",
            { template validRulesUi with DocumentPart = "Attachment"; AttachmentFormat = "Bogus" },
            "Document format 'Bogus' has no domain equivalent."
        ]

    for union, entered, expectedReason in cases do
        match TemplateApiMappers.toUnvalidatedTemplate entered with
        | Error (TemplateRuleShapeInvalid reason) -> Assert.Equal(expectedReason, reason)
        | other -> Assert.Fail($"{union}: expected Error(TemplateRuleShapeInvalid _), but got {other}")

// ---------- error translation ----------

[<Fact; Trait("Level", "Contract")>]
let ``TemplateNameInvalid becomes an unlogged exception carrying the reason`` () =
    let actual = TemplateApiMappers.toMyDogsbodyException anAction (TemplateNameInvalid "Template name must not be empty.")

    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("Template name must not be empty.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``TemplateStoreFailed carries the store's message and is left unmarked, having already been logged by the adapter`` () =
    let actual = TemplateApiMappers.toMyDogsbodyException anAction (TemplateStoreFailed "database is locked")

    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("database is locked", actual.Message)
    Assert.Null actual.InnerException

/// The message, not merely that there is one. tasks.md 8.1 asks for "each `TemplateError` case ->
/// its intended action and MESSAGE", and this test asserted only that the message was non-empty:
/// swapping RequiredFieldHasNoRule's sentence with DuplicateRuleForField's passed the whole suite,
/// 778 of 778. That pair is not cosmetic - the two sentences give opposite instructions ("add a
/// rule" / "remove one"), and this string IS the MudAlert a template author reads when a save is
/// refused, so a wrong one sends them to fix something that is not broken.
///
/// Every case carries its exact sentence here rather than only the seven that had one, so a
/// mis-edited or copy-pasted branch fails on the sentence a user would have read.
[<Fact; Trait("Level", "Contract")>]
let ``every TemplateError case produces its own message and the expected/unexpected split is correct`` () =
    let templateId = TemplateId.create "7" |> valueOrFail
    let supplierId = SupplierId.create "1" |> valueOrFail

    // The four reason-carrying cases pass their reason through verbatim - the constrained type's
    // own create, or a union converter in this file, already wrote the sentence.
    let allCases: (TemplateError * string) list =
        [
            TemplateNameInvalid "a", "a"
            TemplateIdInvalid "b", "b"
            TemplateSupplierIdInvalid "c", "c"
            TemplateRuleShapeInvalid "d", "d"
            PatternInvalid(Reference, "e"), "The pattern for Reference is invalid: e"
            PatternHasNoCaptureGroup Reference, "The pattern for Reference needs a capture group."
            DateFormatInvalid(IssueDate, "f"), "The date format for IssueDate is invalid: f"
            OffsetOutOfRange(Reference, 99), "The offset 99 for Reference must be between 0 and 20."
            LabelIsEmpty Reference, "The label for Reference must not be empty."
            RuleUnreachableForPart(Reference, Body), "The rule for Reference reads a part a Body template never sees."
            RequiredFieldHasNoRule Amount, "Amount needs a rule."
            DuplicateRuleForField Amount, "Amount has more than one rule."
            DerivationSourceMissing IssueDate, "The derived date needs IssueDate to have a rule of its own."
            DerivationSourceNotADate IssueDate,
            "IssueDate is not read as a date, so a date cannot be derived from it."
            DerivationSourceIsSelf DueDate, "DueDate cannot derive its date from itself."
            DerivationUnsupported(Reference, Amount),
            "Reference cannot have its date derived from Amount - only DueDate from IssueDate is supported."
            FieldHintMismatch(Amount, AsText), "Amount cannot be read with the AsText hint."
            ReorderIncomplete [ templateId ], "The new order is missing template(s): 7."
            ReorderDuplicate templateId, "The new order names template '7' more than once."
            TemplateNotFound templateId, "No template was found with id '7'."
            TemplateSupplierNotFound supplierId, "No supplier was found with id '1'."
            TemplateStoreFailed "g", "g"
        ]

    let declaredCases = Reflection.FSharpType.GetUnionCases(typeof<TemplateError>) |> Array.length
    Assert.Equal(declaredCases, List.length allCases)

    // No two cases may share a sentence: an alert that cannot tell two refusals apart is the same
    // failure as an alert naming the wrong one, and a copy-pasted branch shows up here first.
    let distinctSentences =
        allCases |> List.map snd |> List.filter (fun sentence -> sentence.Length > 1) |> List.distinct

    Assert.Equal(
        (allCases |> List.map snd |> List.filter (fun sentence -> sentence.Length > 1) |> List.length),
        List.length distinctSentences
    )

    for case, expectedMessage in allCases do
        let actual = TemplateApiMappers.toMyDogsbodyException anAction case
        Assert.Equal(expectedMessage, actual.Message)
        Assert.Equal(anAction, actual.ActionName)

        // Every case except TemplateStoreFailed is expected/user-caused - wraps an
        // ApplicationException so handleError passes it through unlogged.
        match case with
        | TemplateStoreFailed _ -> Assert.Null actual.InnerException
        | _ -> Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

/// The five cases the mapper had no branch for. An unmatched case does not fall through to a
/// default - it raises MatchFailureException, out of a mapper the UI reaches from Async.Start,
/// where nothing catches it and neither an alert nor a log ever sees it. Each is asserted by its
/// exact sentence rather than merely "did not raise", because the sentence is what the alert says.
[<Fact; Trait("Level", "Contract")>]
let ``the five save-time and reorder refusals become sentences rather than raising`` () =
    let templateId = TemplateId.create "7" |> valueOrFail

    let expectations: (TemplateError * string) list =
        [
            LabelIsEmpty Reference, "The label for Reference must not be empty."
            RuleUnreachableForPart(Reference, Body),
            "The rule for Reference reads a part a Body template never sees."
            DerivationUnsupported(Reference, Amount),
            "Reference cannot have its date derived from Amount - only DueDate from IssueDate is supported."
            FieldHintMismatch(Amount, AsText), "Amount cannot be read with the AsText hint."
            ReorderDuplicate templateId, "The new order names template '7' more than once."
        ]

    for error, expectedMessage in expectations do
        let actual = TemplateApiMappers.toMyDogsbodyException anAction error

        Assert.Equal(anAction, actual.ActionName)
        Assert.Equal(expectedMessage, actual.Message)
        Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

/// The panel's per-field reason is the other outbound translation, and it obeys the same rule as
/// toMyDogsbodyException: a sentence, never `string error`. It also drops the TemplateId every
/// template-carrying case holds - the composition root invents that id for a run that stores
/// nothing, so quoting it would put a template the user never made in front of them.
[<Fact; Trait("Level", "Contract")>]
let ``every InvoiceError case produces a sentence that never quotes the placeholder template id`` () =
    let templateId = TemplateId.create "test" |> valueOrFail
    let supplierId = SupplierId.create "1" |> valueOrFail

    let allCases: InvoiceError list =
        [
            SupplierNotRecognised "billing@acme.test"
            MultipleSuppliersMatched("billing@acme.test", [ supplierId ])
            NoTemplateForSupplier supplierId
            TemplateMatchedNothing(templateId, Amount)
            AmountUnparseable(Amount, "two hundred")
            DateUnparseable(IssueDate, "31/02/2026", "d MMM yyyy")
            DueDateOutOfRange(templateId, DateTime(9999, 12, 31), 30)
            RuleTimedOut(templateId, Reference)
        ]

    let declaredCases =
        Reflection.FSharpType.GetUnionCases(typeof<InvoiceError>) |> Array.length

    Assert.Equal(declaredCases, List.length allCases)

    for case in allCases do
        let actual = TemplateApiMappers.toFieldFailureReason None case
        Assert.False(String.IsNullOrWhiteSpace actual, $"{case} produced an empty reason")
        Assert.DoesNotContain("TemplateId", actual)
        Assert.EndsWith(".", actual)

[<Fact; Trait("Level", "Contract")>]
let ``each InvoiceError the test panel can produce names its field and what the rule found`` () =
    let templateId = TemplateId.create "test" |> valueOrFail

    let expectations: (InvoiceError * string) list =
        [
            TemplateMatchedNothing(templateId, Amount), "The rule for Amount found nothing in the sample text."
            AmountUnparseable(Amount, "two hundred"),
            "The rule for Amount found 'two hundred', which is not a number it can read."
            DateUnparseable(IssueDate, "31/02/2026", "d MMM yyyy"),
            "The rule for IssueDate found '31/02/2026', which does not match the date format 'd MMM yyyy'."
            DueDateOutOfRange(templateId, DateTime(9999, 12, 31), 30),
            "Adding 30 days to the issue date 9999-12-31 runs past the last date a calendar can hold."
            RuleTimedOut(templateId, Reference), "The rule for Reference took too long and was stopped."
        ]

    for error, expectedReason in expectations do
        Assert.Equal(expectedReason, TemplateApiMappers.toFieldFailureReason None error)

/// The "found nothing" sentence is the only one that names WHERE the rule looked, and three of the
/// seven rule kinds do not look at the pasted sample text at all - requirements.md says so of each:
/// SubjectCapture runs "against the message subject, not the document body", AttachmentName
/// "against the attachment's filename, not its content", and FixedValue returns its value "without
/// consulting the text at all". Naming the sample text for those three sent the author to a box
/// that cannot be the cause, on the panel whose whole job is to point at the broken rule.
///
/// One row per FieldRule case plus the no-rule caller, counted against the union by reflection so
/// an eighth kind fails here as well as breaking the mapper's own match.
[<Fact; Trait("Level", "Contract")>]
let ``toFieldFailureReason names the input the rule that found nothing actually reads`` () =
    let templateId = TemplateId.create "test" |> valueOrFail

    let expectations: (FieldRule option * string) list =
        [
            Some (AfterLabel "Total:"), "The rule for Reference found nothing in the sample text."
            Some (LinesAfterLabel("Total:", 1)), "The rule for Reference found nothing in the sample text."
            Some (RegexCapture @"(INV-\d+)"), "The rule for Reference found nothing in the sample text."
            Some (SubjectCapture @"(INV-\d+)"), "The rule for Reference found nothing in the sample subject."
            Some (AttachmentName @"(INV-\d+)"),
            "The rule for Reference found nothing in the sample attachment filename."
            Some (FixedValue ""), "The fixed value for Reference is empty."
            Some (DateFromField IssueDate), "The rule for Reference found nothing in the sample text."
            None, "The rule for Reference found nothing in the sample text."
        ]

    let declaredCases = Reflection.FSharpType.GetUnionCases(typeof<FieldRule>) |> Array.length
    Assert.Equal(declaredCases + 1, List.length expectations)

    for rule, expectedReason in expectations do
        Assert.Equal(
            expectedReason,
            TemplateApiMappers.toFieldFailureReason rule (TemplateMatchedNothing(templateId, Reference))
        )

/// Only the "found nothing" sentence changes with the rule. Every other case already quotes what
/// the rule found, or names no rule at all, so handing one in must not move them - otherwise the
/// rule-aware branch above would have quietly become a second translation of the same error.
[<Fact; Trait("Level", "Contract")>]
let ``a rule changes only the found-nothing sentence, not the other InvoiceError translations`` () =
    let templateId = TemplateId.create "test" |> valueOrFail
    let supplierId = SupplierId.create "1" |> valueOrFail

    let unaffected: InvoiceError list =
        [
            SupplierNotRecognised "billing@acme.test"
            MultipleSuppliersMatched("billing@acme.test", [ supplierId ])
            NoTemplateForSupplier supplierId
            AmountUnparseable(Amount, "two hundred")
            DateUnparseable(IssueDate, "31/02/2026", "d MMM yyyy")
            DueDateOutOfRange(templateId, DateTime(9999, 12, 31), 30)
            RuleTimedOut(templateId, Reference)
        ]

    for error in unaffected do
        Assert.Equal(
            TemplateApiMappers.toFieldFailureReason None error,
            TemplateApiMappers.toFieldFailureReason (Some (SubjectCapture "(x)")) error
        )

/// The panel gets ONE error for a whole run - the engine stops at the first field that fails - so
/// the row that gets the sentence has to be chosen, not assumed. Every apply-time case a test-panel
/// run can produce names its field; the three supplier/template-selection cases name none, which is
/// why this returns an option rather than a field.
[<Fact; Trait("Level", "Contract")>]
let ``toFailingField names the field every apply-time error is about`` () =
    let templateId = TemplateId.create "test" |> valueOrFail
    let supplierId = SupplierId.create "1" |> valueOrFail

    let expectations: (InvoiceError * TargetField option) list =
        [
            SupplierNotRecognised "billing@acme.test", None
            MultipleSuppliersMatched("billing@acme.test", [ supplierId ]), None
            NoTemplateForSupplier supplierId, None
            TemplateMatchedNothing(templateId, Amount), Some Amount
            AmountUnparseable(Amount, "two hundred"), Some Amount
            DateUnparseable(IssueDate, "31/02/2026", "d MMM yyyy"), Some IssueDate
            DueDateOutOfRange(templateId, DateTime(9999, 12, 31), 30), Some DueDate
            RuleTimedOut(templateId, Reference), Some Reference
        ]

    let declaredCases = Reflection.FSharpType.GetUnionCases(typeof<InvoiceError>) |> Array.length
    Assert.Equal(declaredCases, List.length expectations)

    for error, expectedField in expectations do
        Assert.Equal<TargetField option>(expectedField, TemplateApiMappers.toFailingField error)

[<Fact; Trait("Level", "Contract")>]
let ``an adapter exception becomes TemplateStoreFailed carrying its message`` () =
    let adapterFailure =
        MyDogsbodyException(
            ActionNames.MyDogsbody.Database.TemplateStore.getForSupplier,
            "Failed to retrieve templates for supplier.",
            InvalidOperationException "disk gone"
        )

    let actual = TemplateApiMappers.toTemplateError adapterFailure

    Assert.Equal(TemplateStoreFailed "Failed to retrieve templates for supplier.", actual)
