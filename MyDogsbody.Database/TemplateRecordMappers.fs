/// The bottom mapping point for InvoiceTemplates: the split-column SQLite records (int identity,
/// plain strings, nullable payload columns) <-> domain types. Pure - no I/O, no handleError, no
/// Dapper calls. TemplateStore does the talking; this file only translates.
module MyDogsbody.Database.TemplateRecordMappers

open MyDogsbody.Domain
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Database.Models

// Domain -> persistence. Exhaustive: adding a case to DocumentFormat breaks this build.
let toDocumentFormatString (format: DocumentFormat) : string =
    match format with
    | Pdf -> "Pdf"
    | Word -> "Word"
    | PlainText -> "PlainText"
    | EmailBody -> "EmailBody"

let fromDocumentFormatString (value: string) : Result<DocumentFormat, string> =
    match value with
    | "Pdf" -> Ok Pdf
    | "Word" -> Ok Word
    | "PlainText" -> Ok PlainText
    | "EmailBody" -> Ok EmailBody
    | unknown -> Error $"Stored document format '{unknown}' has no domain equivalent."

let toTargetFieldString (field: TargetField) : string =
    match field with
    | Reference -> "Reference"
    | Amount -> "Amount"
    | Currency -> "Currency"
    | IssueDate -> "IssueDate"
    | DueDate -> "DueDate"

let fromTargetFieldString (value: string) : Result<TargetField, string> =
    match value with
    | "Reference" -> Ok Reference
    | "Amount" -> Ok Amount
    | "Currency" -> Ok Currency
    | "IssueDate" -> Ok IssueDate
    | "DueDate" -> Ok DueDate
    | unknown -> Error $"Stored target field '{unknown}' has no domain equivalent."

/// AttachmentFormat is populated only for the Attachment case - split-column encoding, see
/// design.md.
let toDocumentPartColumns (part: DocumentPart) : string * string option =
    match part with
    | Body -> "Body", None
    | AnyPart -> "AnyPart", None
    | Attachment format -> "Attachment", Some (toDocumentFormatString format)

let fromDocumentPartColumns (documentPart: string) (attachmentFormat: string option) : Result<DocumentPart, string> =
    match documentPart, attachmentFormat with
    | "Body", _ -> Ok Body
    | "AnyPart", _ -> Ok AnyPart
    | "Attachment", Some formatString -> fromDocumentFormatString formatString |> Result.map Attachment
    | "Attachment", None -> Error "Stored document part 'Attachment' has no attachment format."
    | unknown, _ -> Error $"Stored document part '{unknown}' has no domain equivalent."

/// RuleText/RuleOffset/RuleSourceField hold whichever payload the case carries and are None
/// otherwise - split-column encoding, see design.md.
let toFieldRuleColumns (rule: FieldRule) : string * string option * int option * string option =
    match rule with
    | AfterLabel label -> "AfterLabel", Some label, None, None
    | LinesAfterLabel(label, offset) -> "LinesAfterLabel", Some label, Some offset, None
    | RegexCapture pattern -> "RegexCapture", Some pattern, None, None
    | FixedValue value -> "FixedValue", Some value, None, None
    | SubjectCapture pattern -> "SubjectCapture", Some pattern, None, None
    | AttachmentName pattern -> "AttachmentName", Some pattern, None, None
    | DateFromField source -> "DateFromField", None, None, Some (toTargetFieldString source)

let fromFieldRuleColumns
    (ruleKind: string)
    (ruleText: string option)
    (ruleOffset: int option)
    (ruleSourceField: string option)
    : Result<FieldRule, string> =
    match ruleKind, ruleText, ruleOffset, ruleSourceField with
    | "AfterLabel", Some label, _, _ -> Ok (AfterLabel label)
    | "LinesAfterLabel", Some label, Some offset, _ -> Ok (LinesAfterLabel(label, offset))
    | "RegexCapture", Some pattern, _, _ -> Ok (RegexCapture pattern)
    | "FixedValue", Some value, _, _ -> Ok (FixedValue value)
    | "SubjectCapture", Some pattern, _, _ -> Ok (SubjectCapture pattern)
    | "AttachmentName", Some pattern, _, _ -> Ok (AttachmentName pattern)
    | "DateFromField", _, _, Some sourceField -> fromTargetFieldString sourceField |> Result.map DateFromField
    | unknown, _, _, _ -> Error $"Stored rule kind '{unknown}' has no domain equivalent, or is missing a required column."

/// HintText holds AsMoney's separator or AsDate's format and is None for AsText - split-column
/// encoding, see design.md.
let toParseHintColumns (hint: ParseHint) : string * string option =
    match hint with
    | AsText -> "AsText", None
    | AsMoney separator -> "AsMoney", Some (string separator)
    | AsDate format -> "AsDate", Some format

let fromParseHintColumns (hintKind: string) (hintText: string option) : Result<ParseHint, string> =
    match hintKind, hintText with
    | "AsText", _ -> Ok AsText
    | "AsMoney", Some separator when separator.Length = 1 -> Ok (AsMoney separator.[0])
    | "AsMoney", _ -> Error "Stored AsMoney hint is missing its decimal separator."
    | "AsDate", Some format -> Ok (AsDate format)
    | "AsDate", None -> Error "Stored AsDate hint is missing its format."
    | unknown, _ -> Error $"Stored hint kind '{unknown}' has no domain equivalent."

let toNewTemplateFieldRuleRecord (templateId: int) (rule: TemplateFieldRule) : TemplateFieldRuleRecord =
    let ruleKind, ruleText, ruleOffset, ruleSourceField = toFieldRuleColumns rule.Rule
    let hintKind, hintText = toParseHintColumns rule.Hint

    {
        Id = 0
        TemplateId = templateId
        TargetField = toTargetFieldString rule.Field
        RuleKind = ruleKind
        RuleText = ruleText
        RuleOffset = ruleOffset
        RuleSourceField = ruleSourceField
        HintKind = hintKind
        HintText = hintText
    }

let fromTemplateFieldRuleRecord (row: TemplateFieldRuleRecord) : Result<TemplateFieldRule, string> =
    result {
        let! field = fromTargetFieldString row.TargetField
        let! rule = fromFieldRuleColumns row.RuleKind row.RuleText row.RuleOffset row.RuleSourceField
        let! hint = fromParseHintColumns row.HintKind row.HintText
        return { Field = field; Rule = rule; Hint = hint }
    }

/// The identifier the domain carries, as the store's own key type. Only ever called on an id
/// that came from a row already read - a value that does not parse is a data-integrity failure,
/// not something a user did, so it is allowed to raise and be caught like any other unexpected
/// adapter failure. Same idiom as SupplierRecordMappers.toRowId.
let toRowId (id: TemplateId) : int = int (TemplateId.value id)

/// Domain -> persistence, for a row the store has not seen before. Id is a placeholder - the
/// insert excludes that column so SQLite assigns it.
let toNewTemplateRecord (supplierId: int) (template: ValidTemplate) : InvoiceTemplateRecord =
    let documentPart, attachmentFormat = toDocumentPartColumns (ValidTemplate.part template)

    {
        Id = 0
        SupplierId = supplierId
        Name = TemplateName.value (ValidTemplate.name template)
        DocumentPart = documentPart
        AttachmentFormat = attachmentFormat
        Position = ValidTemplate.position template
    }

/// Persistence -> domain, whole row plus its field rules. Reconstructing the ValidTemplate
/// recompiles every pattern-carrying rule's pattern rather than reading one back compiled - see
/// ValidateTemplateWorkflow.reconstructValidTemplate.
let toStoredTemplate (row: InvoiceTemplateRecord) (fieldRuleRows: TemplateFieldRuleRecord list) : Result<StoredTemplate, string> =
    result {
        let! id = TemplateId.create (string row.Id)
        let! supplierId = SupplierId.create (string row.SupplierId)
        let! name = TemplateName.create row.Name
        let! part = fromDocumentPartColumns row.DocumentPart row.AttachmentFormat

        let! rules =
            fieldRuleRows
            |> List.fold
                (fun accumulated fieldRuleRow ->
                    accumulated
                    |> Result.bind (fun acc ->
                        fromTemplateFieldRuleRecord fieldRuleRow |> Result.map (fun rule -> rule :: acc)))
                (Ok [])
            |> Result.map List.rev

        let! template = ValidateTemplateWorkflow.reconstructValidTemplate supplierId name part row.Position rules

        return { Id = id; Template = template }
    }
