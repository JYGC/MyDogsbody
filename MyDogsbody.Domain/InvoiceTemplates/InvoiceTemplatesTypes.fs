namespace MyDogsbody.Domain.InvoiceTemplates

open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers

/// The identifier the store assigned. Opaque to the domain, the same way SupplierId is.
type TemplateId = private TemplateId of string

module TemplateId =

    let create (value: string) : Result<TemplateId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Template id must not be empty."
        else
            Ok (TemplateId value)

    let value (TemplateId id) = id

/// Trimmed on the way in. A template's name is display-only - unlike SupplierName, nothing
/// compares it for uniqueness, so it carries no case-insensitivity rule.
type TemplateName = private TemplateName of string

module TemplateName =

    [<Literal>]
    let MaximumLength = 100

    let create (value: string) : Result<TemplateName, string> =
        let trimmed = if isNull value then "" else value.Trim()

        if System.String.IsNullOrWhiteSpace trimmed then
            Error "Template name must not be empty."
        elif trimmed.Length > MaximumLength then
            Error $"Template name must be {MaximumLength} characters or fewer."
        else
            Ok (TemplateName trimmed)

    let value (TemplateName name) = name

type DocumentPart =
    | Body
    | Attachment of DocumentFormat
    | AnyPart

/// The seven kinds, four measured as load-bearing and three added on evidence. Deliberately
/// small - every case is one the page must render an editor for and one the user has to
/// understand.
type FieldRule =
    | AfterLabel      of label: string
    | LinesAfterLabel of label: string * offset: int
    | RegexCapture    of pattern: string
    | FixedValue      of string
    | SubjectCapture  of pattern: string
    | AttachmentName  of pattern: string
    | DateFromField   of source: TargetField

and TargetField = Reference | Amount | Currency | IssueDate | DueDate

type ParseHint =
    | AsText
    | AsMoney of decimalSeparator: char
    | AsDate  of format: string // explicit. NEVER DateTime.Parse with ambient culture

type TemplateFieldRule =
    { Field: TargetField
      Rule: FieldRule
      Hint: ParseHint }

type UnvalidatedTemplate =
    { SupplierId: string
      Name: string
      Part: DocumentPart
      Position: int
      Rules: TemplateFieldRule list }

/// The type that matters. Produced ONLY by ValidateTemplateWorkflow, accepted by the engine and
/// by nothing else. This is where the compile-time guarantee the rest of the domain enjoys is
/// replaced by a runtime boundary - friction #9, and the reason this change is test-heavy.
type ValidTemplate =
    private
        { SupplierId': SupplierId
          Name': TemplateName
          Part': DocumentPart
          Position': int
          Rules': TemplateFieldRule list
          CompiledPatterns': Map<TargetField, System.Text.RegularExpressions.Regex> }

module ValidTemplate =

    // Read-only accessors; no constructor is exposed. ValidateTemplateWorkflow is the only
    // function in this area allowed to build the private record literal.
    let supplierId (validTemplate: ValidTemplate) = validTemplate.SupplierId'
    let name (validTemplate: ValidTemplate) = validTemplate.Name'
    let part (validTemplate: ValidTemplate) = validTemplate.Part'
    let position (validTemplate: ValidTemplate) = validTemplate.Position'
    let rules (validTemplate: ValidTemplate) = validTemplate.Rules'
    let compiledPatterns (validTemplate: ValidTemplate) = validTemplate.CompiledPatterns'

type StoredTemplate =
    { Id: TemplateId
      Template: ValidTemplate }

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - InvoiceTemplatesTypes.fs: TemplateError
type TemplateError =
    | TemplateNameInvalid of reason: string
    | TemplateIdInvalid of reason: string
    | TemplateSupplierIdInvalid of reason: string
    /// A mapping-level concern, distinct from PatternInvalid (a pattern that does not compile) or
    /// DateFormatInvalid (a format string that is not real). Mirrors SupplierError.MatcherInvalid:
    /// Result rather than raising, because the top mapper is called from Async.Start, where an
    /// uncaught exception reaches neither an alert nor a log.
    | TemplateRuleShapeStringFromTheUiHasNoDomainEquivalent of reason: string
    | PatternInvalid of field: TargetField * reason: string
    | PatternHasNoCaptureGroup of field: TargetField
    | DateFormatInvalid of field: TargetField * reason: string
    | OffsetOutOfRange of field: TargetField * offset: int
    | LabelIsEmpty of field: TargetField
    | RuleUnreachableForPart of field: TargetField * part: DocumentPart
    | RequiredFieldHasNoRule of TargetField
    | DuplicateRuleForField of TargetField
    | DerivationSourceMissing of source: TargetField
    | DerivationSourceNotADate of source: TargetField
    | DerivationSourceIsSelf of field: TargetField
    | DerivationUnsupported of field: TargetField * source: TargetField
    | FieldHintMismatch of field: TargetField * hint: ParseHint
    | ReorderIncomplete of missing: TemplateId list
    | ReorderDuplicate of duplicate: TemplateId
    | TemplateNotFound of TemplateId
    | TemplateSupplierNotFound of SupplierId
    | TemplateStoreFailed of message: string

// Dependencies as function types - a workflow receives a function value, so a test supplies a
// lambda and the composition root supplies the real adapter.

type LoadTemplatesForSupplier = SupplierId -> Result<StoredTemplate list, TemplateError>

type SaveTemplate = ValidTemplate -> Result<StoredTemplate, TemplateError>

/// None when no row carried that identifier, so "not found" stays the workflow's decision.
type UpdateTemplate = TemplateId -> ValidTemplate -> Result<StoredTemplate option, TemplateError>

type DeleteTemplate = TemplateId -> Result<bool, TemplateError>

type ReorderTemplates = SupplierId -> TemplateId list -> Result<unit, TemplateError>

/// Declared here rather than reusing the suppliers area's LoadSuppliers, which returns
/// Result<_, SupplierError>: a workflow in this area returns TemplateError, so reusing it would
/// mean a Result.mapError at every call site and one dependency type spanning two error DUs - and
/// therefore owing a contract suite in both areas. The adapter is the same SupplierStore.getAll;
/// only the error mapping in TemplateApiFactory differs.
type LoadSuppliersForTemplates = unit -> Result<StoredSupplier list, TemplateError>
