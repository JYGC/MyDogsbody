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

/// Which part of a message a template reads.
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

/// What the dialog produced. Untrusted - nothing has checked any of this yet.
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
    let supplierId (t: ValidTemplate) = t.SupplierId'
    let name (t: ValidTemplate) = t.Name'
    let part (t: ValidTemplate) = t.Part'
    let position (t: ValidTemplate) = t.Position'
    let rules (t: ValidTemplate) = t.Rules'
    let compiledPatterns (t: ValidTemplate) = t.CompiledPatterns'

/// Been through the store.
type StoredTemplate =
    { Id: TemplateId
      Template: ValidTemplate }

/// Can this template be SAVED? Apply-time failures are InvoiceError, not this - see design.md ->
/// Decisions taken.
///
/// Three cases beyond design.md's original listing, the same way change #1 added
/// SupplierError.PaymentTermInvalid when its documented DU had no case for a failure the
/// workflow actually needed to report:
///  - TemplateSupplierIdInvalid, TemplateIdInvalid: design.md's sequence diagram never shows a
///    "parse this id" step, but EditSupplierWorkflow.validate is the established precedent for
///    this exact shape of problem (an id arriving as an untrusted string) - it parses the id
///    itself and reports SupplierIdInvalid on failure. These mirror that, one for the supplier a
///    template belongs to and one for the template being edited.
///  - DerivationSourceIsSelf: "a DateFromField rule may not name DueDate as its own source"
///    (design.md -> Decisions taken, #9) names a refusal that fits neither
///    DerivationSourceMissing (the source DOES have a rule - itself) nor DerivationSourceNotADate
///    (the question isn't whether the source is date-shaped). Generalised to any field, not only
///    DueDate, since a rule naming itself as its own source is circular regardless of which field.
///  - ReorderIncomplete: requirements.md requires refusing a reorder that omits one of the
///    supplier's existing templates. A submitted id that names no template of this supplier's
///    reuses TemplateNotFound (an exact semantic fit); an existing template left out of the
///    submitted order has no existing case to reuse, so this one carries every id left out, not
///    only the first, the same way MultipleSuppliersMatched carries every match rather than one.
///  - ReorderDuplicate: an order naming the same template twice is neither foreign (the id IS the
///    supplier's) nor incomplete (Set.ofList collapses the repeat, so nothing looks missing), so
///    neither existing case describes it. Carries the repeated id, the same way TemplateNotFound
///    carries the offending one.
type TemplateError =
    | TemplateNameInvalid of reason: string
    | TemplateIdInvalid of reason: string
    | TemplateSupplierIdInvalid of reason: string
    | PatternInvalid of field: TargetField * reason: string
    | PatternHasNoCaptureGroup of field: TargetField
    | DateFormatInvalid of field: TargetField * reason: string
    | OffsetOutOfRange of field: TargetField * offset: int
    | RequiredFieldHasNoRule of TargetField
    | DuplicateRuleForField of TargetField
    | DerivationSourceMissing of source: TargetField
    | DerivationSourceNotADate of source: TargetField
    | DerivationSourceIsSelf of field: TargetField
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

/// The suppliers a template may be written for. Declared here rather than reusing the suppliers
/// area's LoadSuppliers, which returns Result<_, SupplierError>: a workflow in this area returns
/// TemplateError, so reusing it would mean a Result.mapError at every call site and one
/// dependency type spanning two error DUs - and therefore owing a contract suite in both areas.
/// The adapter is the same SupplierStore.getAll; only the error mapping in TemplateApiFactory
/// differs.
type LoadSuppliersForTemplates = unit -> Result<StoredSupplier list, TemplateError>
