module MyDogsbody.Tests.Contracts.TemplateExhaustivenessTests

open Microsoft.FSharp.Reflection
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// A private match ending in a catch-all (`| unknown -> Error $"... has no domain equivalent."`)
// is exhaustive as far as the F# compiler is concerned even when a case is missing from it - the
// compiler warning that would normally catch a forgotten case is silenced by the catch-all. These
// tests close that gap: they reflect each union's case names and probe the string -> domain
// mapping direction with each one, relying on the convention this codebase already follows (see
// toTargetFieldUiString) that a UI string equals its domain case's own name.
//
// Reflection only needs case NAMES here, not sample values - toUnvalidatedTemplate is a shape
// check (does this string have a domain equivalent), not full validation, so a single-rule,
// otherwise-incomplete template is a valid probe.

let private caseNames (unionType: System.Type) : string list =
    FSharpType.GetUnionCases(unionType) |> Array.map (fun c -> c.Name) |> Array.toList

let private aRule ruleKind ruleText ruleOffset ruleSourceField hintKind hintText : TemplateFieldRuleUiType =
    { Field = "Reference"; RuleKind = ruleKind; RuleText = ruleText; RuleOffset = ruleOffset; RuleSourceField = ruleSourceField; HintKind = hintKind; HintText = hintText }

let private aTemplate (rule: TemplateFieldRuleUiType) documentPart attachmentFormat : TemplateUiTypeWithoutId =
    { SupplierId = "1"; Name = "Probe"; DocumentPart = documentPart; AttachmentFormat = attachmentFormat; Position = 0; Rules = [ rule ] }

/// False only for the specific "this string has no domain equivalent" shape-mapping failure -
/// any other Ok/Error outcome means the string WAS recognised as a case, which is all this file
/// checks for.
let private wasRecognised (result: Result<UnvalidatedTemplate, TemplateError>) =
    match result with
    | Error (TemplateRuleShapeInvalid reason) when reason.Contains "has no domain equivalent." -> false
    | _ -> true

// ---------- FieldRule (rule kind) ----------

let fieldRuleCaseNames: obj[] seq = [ for name in caseNames typeof<FieldRule> -> [| box name |] ]

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof fieldRuleCaseNames)>]
let ``every FieldRule case name is recognised by the rule-kind mapper`` (caseName: string) =
    let rule =
        match caseName with
        | "LinesAfterLabel" -> aRule caseName "Invoice:" 1 "" "AsText" ""
        | "DateFromField" -> aRule caseName "" 0 "DueDate" "AsText" ""
        | _ -> aRule caseName "Invoice:" 0 "" "AsText" ""

    let actual = TemplateApiMappers.toUnvalidatedTemplate (aTemplate rule "AnyPart" "")

    Assert.True(wasRecognised actual, $"FieldRule case '{caseName}' was not recognised by the rule-kind mapper")

// ---------- TargetField ----------

let targetFieldCaseNames: obj[] seq = [ for name in caseNames typeof<TargetField> -> [| box name |] ]

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof targetFieldCaseNames)>]
let ``every TargetField case name is recognised by the field mapper`` (caseName: string) =
    let rule = { aRule "AfterLabel" "Invoice:" 0 "" "AsText" "" with Field = caseName }

    let actual = TemplateApiMappers.toUnvalidatedTemplate (aTemplate rule "AnyPart" "")

    Assert.True(wasRecognised actual, $"TargetField case '{caseName}' was not recognised by the field mapper")

/// DateFromField's own source field is a second place a TargetField name is parsed - a case
/// recognised as a Field but not as a RuleSourceField would slip past the previous test.
[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof targetFieldCaseNames)>]
let ``every TargetField case name is recognised as a DateFromField source`` (caseName: string) =
    let rule = aRule "DateFromField" "" 0 caseName "AsDate" "yyyy-MM-dd"

    let actual = TemplateApiMappers.toUnvalidatedTemplate (aTemplate rule "AnyPart" "")

    Assert.True(wasRecognised actual, $"TargetField case '{caseName}' was not recognised as a DateFromField source")

// ---------- ParseHint (hint kind) ----------

let parseHintCaseNames: obj[] seq = [ for name in caseNames typeof<ParseHint> -> [| box name |] ]

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof parseHintCaseNames)>]
let ``every ParseHint case name is recognised by the hint-kind mapper`` (caseName: string) =
    let hintText = match caseName with | "AsMoney" -> "." | "AsDate" -> "yyyy-MM-dd" | _ -> ""
    let rule = aRule "AfterLabel" "Invoice:" 0 "" caseName hintText

    let actual = TemplateApiMappers.toUnvalidatedTemplate (aTemplate rule "AnyPart" "")

    Assert.True(wasRecognised actual, $"ParseHint case '{caseName}' was not recognised by the hint-kind mapper")

// ---------- DocumentPart ----------

let documentPartCaseNames: obj[] seq = [ for name in caseNames typeof<DocumentPart> -> [| box name |] ]

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof documentPartCaseNames)>]
let ``every DocumentPart case name is recognised by the document-part mapper`` (caseName: string) =
    let rule = aRule "AfterLabel" "Invoice:" 0 "" "AsText" ""

    let actual = TemplateApiMappers.toUnvalidatedTemplate (aTemplate rule caseName "Pdf")

    Assert.True(wasRecognised actual, $"DocumentPart case '{caseName}' was not recognised by the document-part mapper")

// ---------- DocumentFormat (only reachable through DocumentPart's Attachment case) ----------

let documentFormatCaseNames: obj[] seq = [ for name in caseNames typeof<DocumentFormat> -> [| box name |] ]

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof documentFormatCaseNames)>]
let ``every DocumentFormat case name is recognised by the attachment-format mapper`` (caseName: string) =
    let rule = aRule "AfterLabel" "Invoice:" 0 "" "AsText" ""

    let actual = TemplateApiMappers.toUnvalidatedTemplate (aTemplate rule "Attachment" caseName)

    Assert.True(wasRecognised actual, $"DocumentFormat case '{caseName}' was not recognised by the attachment-format mapper")
