module MyDogsbody.Tests.Startup.TemplateApiMappersTests

open System
open Xunit
open MyDogsbody.Exceptions.Types
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

[<Fact; Trait("Level", "Contract")>]
let ``every TemplateError case produces a non-empty message and the expected/unexpected split is correct`` () =
    let templateId = TemplateId.create "7" |> valueOrFail
    let supplierId = SupplierId.create "1" |> valueOrFail

    let allCases: TemplateError list =
        [
            TemplateNameInvalid "a"
            TemplateIdInvalid "b"
            TemplateSupplierIdInvalid "c"
            TemplateRuleShapeInvalid "d"
            PatternInvalid(Reference, "e")
            PatternHasNoCaptureGroup Reference
            DateFormatInvalid(IssueDate, "f")
            OffsetOutOfRange(Reference, 99)
            LabelIsEmpty Reference
            RuleUnreachableForPart(Reference, Body)
            RequiredFieldHasNoRule Amount
            DuplicateRuleForField Amount
            DerivationSourceMissing IssueDate
            DerivationSourceNotADate IssueDate
            DerivationSourceIsSelf DueDate
            DerivationUnsupported(Reference, Amount)
            FieldHintMismatch(Amount, AsText)
            ReorderIncomplete [ templateId ]
            ReorderDuplicate templateId
            TemplateNotFound templateId
            TemplateSupplierNotFound supplierId
            TemplateStoreFailed "g"
        ]

    let declaredCases = Reflection.FSharpType.GetUnionCases(typeof<TemplateError>) |> Array.length
    Assert.Equal(declaredCases, List.length allCases)

    for case in allCases do
        let actual = TemplateApiMappers.toMyDogsbodyException anAction case
        Assert.False(String.IsNullOrWhiteSpace actual.Message, $"{case} produced an empty message")
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
        let actual = TemplateApiMappers.toFieldFailureReason case
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
        Assert.Equal(expectedReason, TemplateApiMappers.toFieldFailureReason error)

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
