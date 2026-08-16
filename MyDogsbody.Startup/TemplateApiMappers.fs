/// The top mapping point: domain type <-> MyDogsbody.UI.Types record, plus the translation
/// between the two error types.
///
/// A deliberate cost, same as SupplierApiMappers: a workflow's StoredTemplate could be handed to
/// the UI directly, saving this file - but that would put MyDogsbody.Domain in UI.Portal's
/// reference graph. Keeping the UI on its own records is what makes the domain unreachable from
/// the screen rather than merely unused there.
///
/// Total functions with no module-level bindings, so a test reaches them without Startup.fs
/// opening a database.
module MyDogsbody.Startup.TemplateApiMappers

open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.UI.Types

// Every string->union conversion below returns Result rather than raising - the same reasoning
// as SupplierApiMappers.toMatcherKind: these mappers are called from Async.Start, where an
// uncaught exception reaches neither an alert nor a log.

let private toDocumentFormat (value: string) : Result<DocumentFormat, string> =
    match value with
    | "Pdf" -> Ok Pdf
    | "Word" -> Ok Word
    | "PlainText" -> Ok PlainText
    | "EmailBody" -> Ok EmailBody
    | unknown -> Error $"Document format '{unknown}' has no domain equivalent."

let private toDocumentFormatUiString (format: DocumentFormat) : string =
    match format with
    | Pdf -> "Pdf"
    | Word -> "Word"
    | PlainText -> "PlainText"
    | EmailBody -> "EmailBody"

let private toDocumentPart (documentPart: string) (attachmentFormat: string) : Result<DocumentPart, string> =
    match documentPart with
    | "Body" -> Ok Body
    | "AnyPart" -> Ok AnyPart
    | "Attachment" -> toDocumentFormat attachmentFormat |> Result.map Attachment
    | unknown -> Error $"Document part '{unknown}' has no domain equivalent."

let private toDocumentPartUiColumns (part: DocumentPart) : string * string =
    match part with
    | Body -> "Body", ""
    | AnyPart -> "AnyPart", ""
    | Attachment format -> "Attachment", toDocumentFormatUiString format

let private toTargetField (value: string) : Result<TargetField, string> =
    match value with
    | "Reference" -> Ok Reference
    | "Amount" -> Ok Amount
    | "Currency" -> Ok Currency
    | "IssueDate" -> Ok IssueDate
    | "DueDate" -> Ok DueDate
    | unknown -> Error $"Target field '{unknown}' has no domain equivalent."

/// Not private: TemplateApiFactory's TestTemplate reuses this for FieldTestResultUiType.Field.
let toTargetFieldUiString (field: TargetField) : string =
    match field with
    | Reference -> "Reference"
    | Amount -> "Amount"
    | Currency -> "Currency"
    | IssueDate -> "IssueDate"
    | DueDate -> "DueDate"

let private toFieldRule
    (ruleKind: string)
    (ruleText: string)
    (ruleOffset: int)
    (ruleSourceField: string)
    : Result<FieldRule, string> =
    match ruleKind with
    | "AfterLabel" -> Ok (AfterLabel ruleText)
    | "LinesAfterLabel" -> Ok (LinesAfterLabel(ruleText, ruleOffset))
    | "RegexCapture" -> Ok (RegexCapture ruleText)
    | "FixedValue" -> Ok (FixedValue ruleText)
    | "SubjectCapture" -> Ok (SubjectCapture ruleText)
    | "AttachmentName" -> Ok (AttachmentName ruleText)
    | "DateFromField" -> toTargetField ruleSourceField |> Result.map DateFromField
    | unknown -> Error $"Rule kind '{unknown}' has no domain equivalent."

let private toFieldRuleUiColumns (rule: FieldRule) : string * string * int * string =
    match rule with
    | AfterLabel label -> "AfterLabel", label, 0, ""
    | LinesAfterLabel(label, offset) -> "LinesAfterLabel", label, offset, ""
    | RegexCapture pattern -> "RegexCapture", pattern, 0, ""
    | FixedValue value -> "FixedValue", value, 0, ""
    | SubjectCapture pattern -> "SubjectCapture", pattern, 0, ""
    | AttachmentName pattern -> "AttachmentName", pattern, 0, ""
    | DateFromField source -> "DateFromField", "", 0, toTargetFieldUiString source

/// The AsMoney branch is the one place this file READS a UI-supplied string rather than matching
/// on it, so it is the one place null had to be named. `match` against a null string is an
/// equality test and falls through to `unknown` harmlessly; `hintText.Length` is a dereference,
/// and it raised NullReferenceException out of AddTemplate, EditTemplate and TestTemplate alike -
/// past the very Async.Start boundary the header comment above says this returns Result for. A
/// cleared MudTextField hands a bound `string` back as null, so an emptied separator box is the
/// ordinary way it arrives, not a contrived one.
let private toParseHint (hintKind: string) (hintText: string) : Result<ParseHint, string> =
    match hintKind with
    | "AsText" -> Ok AsText
    | "AsMoney" when not (isNull hintText) && hintText.Length = 1 -> Ok (AsMoney hintText.[0])
    | "AsMoney" -> Error "AsMoney hint is missing its decimal separator."
    | "AsDate" -> Ok (AsDate hintText)
    | unknown -> Error $"Hint kind '{unknown}' has no domain equivalent."

let private toParseHintUiColumns (hint: ParseHint) : string * string =
    match hint with
    | AsText -> "AsText", ""
    | AsMoney separator -> "AsMoney", string separator
    | AsDate format -> "AsDate", format

let private toTemplateFieldRule (uiRule: TemplateFieldRuleUiType) : Result<TemplateFieldRule, TemplateError> =
    result {
        let! field = toTargetField uiRule.Field |> Result.mapError TemplateRuleShapeInvalid
        let! rule = toFieldRule uiRule.RuleKind uiRule.RuleText uiRule.RuleOffset uiRule.RuleSourceField |> Result.mapError TemplateRuleShapeInvalid
        let! hint = toParseHint uiRule.HintKind uiRule.HintText |> Result.mapError TemplateRuleShapeInvalid
        return { Field = field; Rule = rule; Hint = hint }
    }

let private toTemplateFieldRuleUiType (rule: TemplateFieldRule) : TemplateFieldRuleUiType =
    let ruleKind, ruleText, ruleOffset, ruleSourceField = toFieldRuleUiColumns rule.Rule
    let hintKind, hintText = toParseHintUiColumns rule.Hint

    {
        Field = toTargetFieldUiString rule.Field
        RuleKind = ruleKind
        RuleText = ruleText
        RuleOffset = ruleOffset
        RuleSourceField = ruleSourceField
        HintKind = hintKind
        HintText = hintText
    }

/// Stops at the first unrecognised shape, the same way SupplierApiMappers.toUnvalidatedMatchers
/// stops at the first invalid matcher.
let private toUnvalidatedRules (rules: TemplateFieldRuleUiType list) : Result<TemplateFieldRule list, TemplateError> =
    let rec loop remaining acc =
        match remaining with
        | [] -> Ok (List.rev acc)
        | uiRule :: rest ->
            match toTemplateFieldRule uiRule with
            | Error error -> Error error
            | Ok rule -> loop rest (rule :: acc)

    loop rules []

let toUnvalidatedTemplate (uiType: TemplateUiTypeWithoutId) : Result<UnvalidatedTemplate, TemplateError> =
    result {
        let! part = toDocumentPart uiType.DocumentPart uiType.AttachmentFormat |> Result.mapError TemplateRuleShapeInvalid
        let! rules = toUnvalidatedRules uiType.Rules

        return
            {
                SupplierId = uiType.SupplierId
                Name = uiType.Name
                Part = part
                Position = uiType.Position
                Rules = rules
            }
    }

/// Splits the UI record's embedded Id from the rest, matching EditTemplateWorkflow.editTemplate's
/// own signature: the target id travels as its own parameter, separate from the payload.
let toUnvalidatedTemplateEdit (uiType: TemplateUiType) : Result<string * UnvalidatedTemplate, TemplateError> =
    let withoutId: TemplateUiTypeWithoutId =
        {
            SupplierId = uiType.SupplierId
            Name = uiType.Name
            DocumentPart = uiType.DocumentPart
            AttachmentFormat = uiType.AttachmentFormat
            Position = uiType.Position
            Rules = uiType.Rules
        }

    toUnvalidatedTemplate withoutId |> Result.map (fun unvalidated -> uiType.Id, unvalidated)

let toUiType (stored: StoredTemplate) : TemplateUiType =
    let documentPart, attachmentFormat = toDocumentPartUiColumns (ValidTemplate.part stored.Template)

    {
        Id = TemplateId.value stored.Id
        SupplierId = SupplierId.value (ValidTemplate.supplierId stored.Template)
        Name = TemplateName.value (ValidTemplate.name stored.Template)
        DocumentPart = documentPart
        AttachmentFormat = attachmentFormat
        Position = ValidTemplate.position stored.Template
        Rules = ValidTemplate.rules stored.Template |> List.map toTemplateFieldRuleUiType
    }

/// Outbound: a domain error case becomes the exception the UI renders as a sentence.
///
/// Nothing here logs, in either branch - see SupplierApiMappers.toMyDogsbodyException for why.
/// Every case but TemplateStoreFailed wraps an ApplicationException, marking it for
/// ExceptionHelpers.isApplicationException so handleError passes it through unlogged.
let toMyDogsbodyException (action: string) (error: TemplateError) : MyDogsbodyException =
    let expected (message: string) =
        MyDogsbodyException(action, message, System.ApplicationException message)

    match error with
    | TemplateNameInvalid reason -> expected reason
    | TemplateIdInvalid reason -> expected reason
    | TemplateSupplierIdInvalid reason -> expected reason
    | TemplateRuleShapeInvalid reason -> expected reason
    | PatternInvalid(field, reason) -> expected $"The pattern for {toTargetFieldUiString field} is invalid: {reason}"
    | PatternHasNoCaptureGroup field -> expected $"The pattern for {toTargetFieldUiString field} needs a capture group."
    | DateFormatInvalid(field, reason) -> expected $"The date format for {toTargetFieldUiString field} is invalid: {reason}"
    | OffsetOutOfRange(field, offset) -> expected $"The offset {offset} for {toTargetFieldUiString field} must be between 0 and 20."
    | LabelIsEmpty field -> expected $"The label for {toTargetFieldUiString field} must not be empty."
    | RuleUnreachableForPart(field, part) ->
        let partName, _ = toDocumentPartUiColumns part
        expected $"The rule for {toTargetFieldUiString field} reads a part a {partName} template never sees."
    | RequiredFieldHasNoRule field -> expected $"{toTargetFieldUiString field} needs a rule."
    | DuplicateRuleForField field -> expected $"{toTargetFieldUiString field} has more than one rule."
    | DerivationSourceMissing source -> expected $"The derived date needs {toTargetFieldUiString source} to have a rule of its own."
    | DerivationSourceNotADate source -> expected $"{toTargetFieldUiString source} is not read as a date, so a date cannot be derived from it."
    | DerivationSourceIsSelf field -> expected $"{toTargetFieldUiString field} cannot derive its date from itself."
    | DerivationUnsupported(field, source) ->
        expected
            $"{toTargetFieldUiString field} cannot have its date derived from {toTargetFieldUiString source} - only DueDate from IssueDate is supported."
    | FieldHintMismatch(field, hint) ->
        let hintName, _ = toParseHintUiColumns hint
        expected $"{toTargetFieldUiString field} cannot be read with the {hintName} hint."
    | ReorderIncomplete missing ->
        let names = missing |> List.map TemplateId.value |> String.concat ", "
        expected $"The new order is missing template(s): {names}."
    | ReorderDuplicate duplicate -> expected $"The new order names template '{TemplateId.value duplicate}' more than once."
    | TemplateNotFound id -> expected $"No template was found with id '{TemplateId.value id}'."
    | TemplateSupplierNotFound id -> expected $"No supplier was found with id '{SupplierId.value id}'."
    | TemplateStoreFailed message -> MyDogsbodyException(action, message)

/// Inbound: an adapter's exception becomes the one domain case that stands for infrastructure
/// failure. The adapter's handleError has already logged it, so nothing logs again here.
let toTemplateError (ex: MyDogsbodyException) : TemplateError = TemplateStoreFailed ex.Message

/// The other outbound translation: an InvoiceError becomes the sentence the test panel prints
/// against a field. InvoiceError carries no ActionName and never reaches handleError in this
/// change (design.md -> Errors), so it does not go through toMyDogsbodyException - but it is
/// still a domain error the user reads, and the same rule applies: a sentence, not a union.
///
/// `string error` was what this replaced, and it printed
/// `TemplateMatchedNothing (TemplateId "test", Amount)` - a union dump quoting the placeholder
/// TemplateId the composition root invents for a run that never touches the store. requirements.md
/// asks the panel to say, where a field failed, WHY; the id of a template that does not exist is
/// not part of that answer, so every case below drops it and names the field instead.
///
/// A match rather than a catch-all: an InvoiceError case added later breaks the build here, the
/// same way toMyDogsbodyException's exhaustiveness is what caught five missing TemplateError
/// branches in the first review round.
/// Which field an apply-time error is ABOUT.
///
/// ApplyTemplateWorkflow evaluates the fields in one fixed order and stops at the first that
/// fails, so a whole test-panel run hands back a single InvoiceError - and a panel that gave that
/// one error to every row told the author "Reference failed" about a rule that had just matched,
/// with a reason naming Amount. requirements.md asks the panel to show, per field, "where it
/// failed - why", and the panel's entire job is to send the author to the rule that is broken; the
/// reason has to land on the field it names.
///
/// DueDateOutOfRange carries a TemplateId rather than a field, but it is raised in exactly one
/// place - the DueDate DateFromField branch - so the field is DueDate by construction. The three
/// supplier/template-selection cases name no field and cannot arise from a panel run at all, so
/// they answer None and their sentence goes on every row, as before.
///
/// A match rather than a catch-all, for the same reason toFieldFailureReason below is one: an
/// InvoiceError case added later breaks the build here rather than quietly answering None.
let toFailingField (error: InvoiceError) : TargetField option =
    match error with
    | SupplierNotRecognised _
    | MultipleSuppliersMatched _
    | NoTemplateForSupplier _ -> None
    | TemplateMatchedNothing(_, field) -> Some field
    | AmountUnparseable(field, _) -> Some field
    | DateUnparseable(field, _, _) -> Some field
    | DueDateOutOfRange _ -> Some DueDate
    | RuleTimedOut(_, field) -> Some field

let toFieldFailureReason (error: InvoiceError) : string =
    match error with
    // The three MatchSupplierWorkflow / SelectTemplateWorkflow cases cannot arise from a test-panel
    // run - it applies one named template to pasted text and matches no supplier - but they are
    // InvoiceError cases, so they carry a sentence rather than a hole for a later caller to find.
    | SupplierNotRecognised sender -> $"No supplier matched the sender '{sender}'."
    | MultipleSuppliersMatched(sender, suppliers) ->
        let ids = suppliers |> List.map SupplierId.value |> String.concat ", "
        $"More than one supplier matched the sender '{sender}': {ids}."
    | NoTemplateForSupplier supplierId -> $"No template is set up for supplier '{SupplierId.value supplierId}'."
    | TemplateMatchedNothing(_, field) -> $"The rule for {toTargetFieldUiString field} found nothing in the sample text."
    | AmountUnparseable(field, raw) ->
        $"The rule for {toTargetFieldUiString field} found '{raw}', which is not a number it can read."
    | DateUnparseable(field, raw, format) ->
        $"The rule for {toTargetFieldUiString field} found '{raw}', which does not match the date format '{format}'."
    | DueDateOutOfRange(_, issueDate, paymentTermDays) ->
        let issued = issueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        $"Adding {paymentTermDays} days to the issue date {issued} runs past the last date a calendar can hold."
    | RuleTimedOut(_, field) -> $"The rule for {toTargetFieldUiString field} took too long and was stopped."
