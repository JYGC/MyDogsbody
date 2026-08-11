module MyDogsbody.Tests.Startup.TemplateApiMappersTests

open System
open Xunit
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
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
            RequiredFieldHasNoRule Amount
            DuplicateRuleForField Amount
            DerivationSourceMissing IssueDate
            DerivationSourceNotADate IssueDate
            DerivationSourceIsSelf DueDate
            ReorderIncomplete [ templateId ]
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
