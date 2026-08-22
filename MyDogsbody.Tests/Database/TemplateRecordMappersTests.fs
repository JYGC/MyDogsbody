module MyDogsbody.Tests.Database.TemplateRecordMappersTests

open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Database
open MyDogsbody.Database.Models

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

// ---------- DocumentFormat <-> string ----------

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Pdf")>]
[<InlineData("Word")>]
[<InlineData("PlainText")>]
[<InlineData("EmailBody")>]
let ``every DocumentFormat case survives the string round trip`` (expectedString: string) =
    let format =
        match expectedString with
        | "Pdf" -> Pdf
        | "Word" -> Word
        | "PlainText" -> PlainText
        | "EmailBody" -> EmailBody
        | other -> failwith $"unexpected test case {other}"

    Assert.Equal(expectedString, TemplateRecordMappers.toDocumentFormatString format)
    Assert.Equal(Ok format, TemplateRecordMappers.fromDocumentFormatString expectedString)

[<Fact; Trait("Level", "Unit")>]
let ``fromDocumentFormatString rejects a value no build ever declared`` () =
    match TemplateRecordMappers.fromDocumentFormatString "Bogus" with
    | Error reason -> Assert.Equal("Stored document format 'Bogus' has no domain equivalent.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- TargetField <-> string ----------

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Reference")>]
[<InlineData("Amount")>]
[<InlineData("Currency")>]
[<InlineData("IssueDate")>]
[<InlineData("DueDate")>]
let ``every TargetField case survives the string round trip`` (expectedString: string) =
    let field =
        match expectedString with
        | "Reference" -> Reference
        | "Amount" -> Amount
        | "Currency" -> Currency
        | "IssueDate" -> IssueDate
        | "DueDate" -> DueDate
        | other -> failwith $"unexpected test case {other}"

    Assert.Equal(expectedString, TemplateRecordMappers.toTargetFieldString field)
    Assert.Equal(Ok field, TemplateRecordMappers.fromTargetFieldString expectedString)

[<Fact; Trait("Level", "Unit")>]
let ``fromTargetFieldString rejects a value no build ever declared`` () =
    match TemplateRecordMappers.fromTargetFieldString "Bogus" with
    | Error reason -> Assert.Equal("Stored target field 'Bogus' has no domain equivalent.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- DocumentPart <-> (DocumentPart column, AttachmentFormat column) ----------

[<Fact; Trait("Level", "Unit")>]
let ``DocumentPart.Body maps to its columns and back`` () =
    let columns = TemplateRecordMappers.toDocumentPartColumns Body
    Assert.Equal(("Body", None), columns)
    Assert.Equal(Ok Body, TemplateRecordMappers.fromDocumentPartColumns (fst columns) (snd columns))

[<Fact; Trait("Level", "Unit")>]
let ``DocumentPart.AnyPart maps to its columns and back`` () =
    let columns = TemplateRecordMappers.toDocumentPartColumns AnyPart
    Assert.Equal(("AnyPart", None), columns)
    Assert.Equal(Ok AnyPart, TemplateRecordMappers.fromDocumentPartColumns (fst columns) (snd columns))

[<Fact; Trait("Level", "Unit")>]
let ``DocumentPart.Attachment carries its format through both columns and back`` () =
    let columns = TemplateRecordMappers.toDocumentPartColumns (Attachment Pdf)
    Assert.Equal(("Attachment", Some "Pdf"), columns)
    Assert.Equal(Ok (Attachment Pdf), TemplateRecordMappers.fromDocumentPartColumns (fst columns) (snd columns))

[<Fact; Trait("Level", "Unit")>]
let ``fromDocumentPartColumns rejects Attachment with no stored format`` () =
    match TemplateRecordMappers.fromDocumentPartColumns "Attachment" None with
    | Error reason -> Assert.Equal("Stored document part 'Attachment' has no attachment format.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``fromDocumentPartColumns rejects a document part no build ever declared`` () =
    match TemplateRecordMappers.fromDocumentPartColumns "Bogus" None with
    | Error reason -> Assert.Equal("Stored document part 'Bogus' has no domain equivalent.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- FieldRule <-> (RuleKind, RuleText, RuleOffset, RuleSourceField) columns ----------

[<Fact; Trait("Level", "Unit")>]
let ``FieldRule.AfterLabel maps to its columns and back`` () =
    let kind, text, offset, source = TemplateRecordMappers.toFieldRuleColumns (AfterLabel "Total:")
    Assert.Equal(("AfterLabel", Some "Total:", None, None), (kind, text, offset, source))
    Assert.Equal(Ok (AfterLabel "Total:"), TemplateRecordMappers.fromFieldRuleColumns kind text offset source)

[<Fact; Trait("Level", "Unit")>]
let ``FieldRule.LinesAfterLabel carries its label and offset through both columns and back`` () =
    let kind, text, offset, source = TemplateRecordMappers.toFieldRuleColumns (LinesAfterLabel("Total", 1))
    Assert.Equal(("LinesAfterLabel", Some "Total", Some 1, None), (kind, text, offset, source))
    Assert.Equal(Ok (LinesAfterLabel("Total", 1)), TemplateRecordMappers.fromFieldRuleColumns kind text offset source)

[<Fact; Trait("Level", "Unit")>]
let ``FieldRule.RegexCapture maps to its columns and back`` () =
    let kind, text, offset, source = TemplateRecordMappers.toFieldRuleColumns (RegexCapture @"INV-(\d+)")
    Assert.Equal(("RegexCapture", Some @"INV-(\d+)", None, None), (kind, text, offset, source))
    Assert.Equal(Ok (RegexCapture @"INV-(\d+)"), TemplateRecordMappers.fromFieldRuleColumns kind text offset source)

[<Fact; Trait("Level", "Unit")>]
let ``FieldRule.FixedValue maps to its columns and back`` () =
    let kind, text, offset, source = TemplateRecordMappers.toFieldRuleColumns (FixedValue "AUD")
    Assert.Equal(("FixedValue", Some "AUD", None, None), (kind, text, offset, source))
    Assert.Equal(Ok (FixedValue "AUD"), TemplateRecordMappers.fromFieldRuleColumns kind text offset source)

[<Fact; Trait("Level", "Unit")>]
let ``FieldRule.SubjectCapture maps to its columns and back`` () =
    let kind, text, offset, source = TemplateRecordMappers.toFieldRuleColumns (SubjectCapture @"Ref (\w+)")
    Assert.Equal(("SubjectCapture", Some @"Ref (\w+)", None, None), (kind, text, offset, source))
    Assert.Equal(Ok (SubjectCapture @"Ref (\w+)"), TemplateRecordMappers.fromFieldRuleColumns kind text offset source)

[<Fact; Trait("Level", "Unit")>]
let ``FieldRule.AttachmentName maps to its columns and back`` () =
    let kind, text, offset, source = TemplateRecordMappers.toFieldRuleColumns (AttachmentName @"^(\d+)\.pdf$")
    Assert.Equal(("AttachmentName", Some @"^(\d+)\.pdf$", None, None), (kind, text, offset, source))
    Assert.Equal(Ok (AttachmentName @"^(\d+)\.pdf$"), TemplateRecordMappers.fromFieldRuleColumns kind text offset source)

[<Fact; Trait("Level", "Unit")>]
let ``FieldRule.DateFromField carries its source field through the source column and back`` () =
    let kind, text, offset, source = TemplateRecordMappers.toFieldRuleColumns (DateFromField IssueDate)
    Assert.Equal(("DateFromField", None, None, Some "IssueDate"), (kind, text, offset, source))
    Assert.Equal(Ok (DateFromField IssueDate), TemplateRecordMappers.fromFieldRuleColumns kind text offset source)

[<Fact; Trait("Level", "Unit")>]
let ``fromFieldRuleColumns rejects a rule kind no build ever declared`` () =
    match TemplateRecordMappers.fromFieldRuleColumns "Bogus" None None None with
    | Error reason -> Assert.Contains("Bogus", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- ParseHint <-> (HintKind, HintText) columns ----------

[<Fact; Trait("Level", "Unit")>]
let ``ParseHint.AsText maps to its columns and back`` () =
    let kind, text = TemplateRecordMappers.toParseHintColumns AsText
    Assert.Equal(("AsText", None), (kind, text))
    Assert.Equal(Ok AsText, TemplateRecordMappers.fromParseHintColumns kind text)

[<Fact; Trait("Level", "Unit")>]
let ``ParseHint.AsMoney carries its decimal separator through the hint text column and back`` () =
    let kind, text = TemplateRecordMappers.toParseHintColumns (AsMoney ',')
    Assert.Equal(("AsMoney", Some ","), (kind, text))
    Assert.Equal(Ok (AsMoney ','), TemplateRecordMappers.fromParseHintColumns kind text)

[<Fact; Trait("Level", "Unit")>]
let ``ParseHint.AsDate carries its format through the hint text column and back`` () =
    let kind, text = TemplateRecordMappers.toParseHintColumns (AsDate "d MMM yyyy")
    Assert.Equal(("AsDate", Some "d MMM yyyy"), (kind, text))
    Assert.Equal(Ok (AsDate "d MMM yyyy"), TemplateRecordMappers.fromParseHintColumns kind text)

[<Fact; Trait("Level", "Unit")>]
let ``fromParseHintColumns rejects a hint kind no build ever declared`` () =
    match TemplateRecordMappers.fromParseHintColumns "Bogus" None with
    | Error reason -> Assert.Contains("Bogus", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- toStoredTemplate / toNewTemplateRecord / TemplateFieldRuleRecord round trips ----------

let private sampleRules =
    [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
      { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
      { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]

let private sampleValidTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = "1"; Name = "Monthly statement"; Part = AnyPart; Position = 2; Rules = sampleRules }

    match ValidateTemplateWorkflow.validateTemplate unvalidated with
    | Ok template -> template
    | Error error -> failwith $"Test setup produced an invalid template: {error}"

[<Fact; Trait("Level", "Unit")>]
let ``toNewTemplateRecord carries every field of a ValidTemplate, with Id left for the store to assign`` () =
    let actual = TemplateRecordMappers.toNewTemplateRecord 1 sampleValidTemplate

    Assert.Equal(0, actual.Id)
    Assert.Equal(1, actual.SupplierId)
    Assert.Equal("Monthly statement", actual.Name)
    Assert.Equal("AnyPart", actual.DocumentPart)
    Assert.Equal(None, actual.AttachmentFormat)
    Assert.Equal(2, actual.Position)

[<Fact; Trait("Level", "Unit")>]
let ``toNewTemplateFieldRuleRecord and fromTemplateFieldRuleRecord round trip every field`` () =
    let rule = { Field = Amount; Rule = LinesAfterLabel("Total", 1); Hint = AsMoney ',' }
    let record = TemplateRecordMappers.toNewTemplateFieldRuleRecord 5 rule

    Assert.Equal(0, record.Id)
    Assert.Equal(5, record.TemplateId)
    Assert.Equal("Amount", record.TargetField)
    Assert.Equal("LinesAfterLabel", record.RuleKind)
    Assert.Equal(Some "Total", record.RuleText)
    Assert.Equal(Some 1, record.RuleOffset)
    Assert.Equal(None, record.RuleSourceField)
    Assert.Equal("AsMoney", record.HintKind)
    Assert.Equal(Some ",", record.HintText)

    Assert.Equal(Ok rule, TemplateRecordMappers.fromTemplateFieldRuleRecord record)

[<Fact; Trait("Level", "Unit")>]
let ``toStoredTemplate carries every field of a row with its rules`` () =
    let templateRow: InvoiceTemplateRecord =
        { Id = 7; SupplierId = 1; Name = "Monthly statement"; DocumentPart = "AnyPart"; AttachmentFormat = None; Position = 2 }
    let ruleRows =
        [ { Id = 1; TemplateId = 7; TargetField = "Reference"; RuleKind = "AfterLabel"; RuleText = Some "Invoice:"; RuleOffset = None; RuleSourceField = None; HintKind = "AsText"; HintText = None }
          { Id = 2; TemplateId = 7; TargetField = "Amount"; RuleKind = "AfterLabel"; RuleText = Some "Total:"; RuleOffset = None; RuleSourceField = None; HintKind = "AsMoney"; HintText = Some "." }
          { Id = 3; TemplateId = 7; TargetField = "Currency"; RuleKind = "FixedValue"; RuleText = Some "AUD"; RuleOffset = None; RuleSourceField = None; HintKind = "AsText"; HintText = None } ]

    let actual = TemplateRecordMappers.toStoredTemplate templateRow ruleRows

    match actual with
    | Ok stored ->
        Assert.Equal("7", TemplateId.value stored.Id)
        Assert.Equal("1", SupplierId.value (ValidTemplate.supplierId stored.Template))
        Assert.Equal("Monthly statement", TemplateName.value (ValidTemplate.name stored.Template))
        Assert.Equal<DocumentPart>(AnyPart, ValidTemplate.part stored.Template)
        Assert.Equal(2, ValidTemplate.position stored.Template)
        Assert.Equal(3, (ValidTemplate.rules stored.Template).Length)
        Assert.Equal<TargetField list>(
            [ Reference; Amount; Currency ],
            (ValidTemplate.rules stored.Template) |> List.map (fun r -> r.Field)
        )
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``toStoredTemplate recompiles a stored pattern-based rule into a usable compiled Regex`` () =
    let templateRow: InvoiceTemplateRecord =
        { Id = 1; SupplierId = 1; Name = "T"; DocumentPart = "AnyPart"; AttachmentFormat = None; Position = 0 }
    let ruleRows =
        [ { Id = 1; TemplateId = 1; TargetField = "Reference"; RuleKind = "RegexCapture"; RuleText = Some @"INV-(\d+)"; RuleOffset = None; RuleSourceField = None; HintKind = "AsText"; HintText = None }
          { Id = 2; TemplateId = 1; TargetField = "Amount"; RuleKind = "FixedValue"; RuleText = Some "1.00"; RuleOffset = None; RuleSourceField = None; HintKind = "AsMoney"; HintText = Some "." }
          { Id = 3; TemplateId = 1; TargetField = "Currency"; RuleKind = "FixedValue"; RuleText = Some "AUD"; RuleOffset = None; RuleSourceField = None; HintKind = "AsText"; HintText = None } ]

    let actual = TemplateRecordMappers.toStoredTemplate templateRow ruleRows

    match actual with
    | Ok stored ->
        let compiled = ValidTemplate.compiledPatterns stored.Template
        Assert.True(Map.containsKey Reference compiled)
        let regexMatch = (Map.find Reference compiled).Match("INV-9988")
        Assert.True(regexMatch.Success)
        Assert.Equal("9988", regexMatch.Groups.[1].Value)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``toStoredTemplate reports an error rather than a default for an unrecognised stored string`` () =
    let templateRow: InvoiceTemplateRecord =
        { Id = 1; SupplierId = 1; Name = "T"; DocumentPart = "Bogus"; AttachmentFormat = None; Position = 0 }

    let actual = TemplateRecordMappers.toStoredTemplate templateRow []

    match actual with
    | Error reason -> Assert.Equal("Stored document part 'Bogus' has no domain equivalent.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
