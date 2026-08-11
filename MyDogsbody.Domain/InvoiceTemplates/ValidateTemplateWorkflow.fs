module MyDogsbody.Domain.InvoiceTemplates.ValidateTemplateWorkflow

open System
open System.Text.RegularExpressions
open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

/// Compiles a user-typed pattern with a match timeout, preferring the NonBacktracking engine and
/// falling back to the classic backtracking engine - still with the timeout - for constructs
/// NonBacktracking does not support (lookaround, backreferences). The timeout is the actual
/// availability guarantee; NonBacktracking is only the cheap way to make it unnecessary for most
/// patterns.
///
/// NonBacktracking rejecting a construct throws NotSupportedException, not ArgumentException -
/// verified empirically, since a plausible-looking version of this function that caught
/// ArgumentException instead would let that exception escape uncaught. A malformed pattern throws
/// RegexParseException (itself an ArgumentException) on either engine.
///
/// Whether the result used the fallback is readable off its own Options - HasFlag
/// RegexOptions.NonBacktracking - so no separate flag needs to be threaded through this result.
let compilePattern (pattern: string) : Result<Regex, string> =
    let timeout = TimeSpan.FromMilliseconds 250.0

    try
        Ok (Regex(pattern, RegexOptions.NonBacktracking ||| RegexOptions.IgnoreCase, timeout))
    with
    | :? NotSupportedException ->
        try
            Ok (Regex(pattern, RegexOptions.IgnoreCase, timeout))
        with :? RegexParseException as ex ->
            Error ex.Message
    | :? RegexParseException as ex ->
        Error ex.Message

let private hasCaptureGroup (regex: Regex) : bool =
    regex.GetGroupNumbers() |> Array.exists (fun number -> number > 0)

[<Literal>]
let private MinimumOffset = 0

[<Literal>]
let private MaximumOffset = 20

let private requiredFields = [ Reference; Amount; Currency ]

let private ensureRequiredFieldsHaveRules (rules: TemplateFieldRule list) : Result<unit, TemplateError> =
    let covered = rules |> List.map (fun rule -> rule.Field) |> Set.ofList

    match requiredFields |> List.tryFind (fun field -> not (covered.Contains field)) with
    | Some missing -> Error (RequiredFieldHasNoRule missing)
    | None -> Ok ()

let private ensureNoDuplicateField (rules: TemplateFieldRule list) : Result<unit, TemplateError> =
    let duplicate =
        rules
        |> List.map (fun rule -> rule.Field)
        |> List.countBy id
        |> List.tryFind (fun (_, count) -> count > 1)

    match duplicate with
    | Some (field, _) -> Error (DuplicateRuleForField field)
    | None -> Ok ()

/// Whether a field's own rule reads as a date - what a DateFromField source must be, or the
/// derivation could never produce a date to add the payment term to.
let private isDateHinted (rule: TemplateFieldRule) : bool =
    match rule.Hint with
    | AsDate _ -> true
    | AsText | AsMoney _ -> false

/// Cross-rule DateFromField checks - these need the whole rule set as context, unlike the
/// per-rule checks in validateRule. Checked in the order design.md's sequence diagram states:
/// source exists, is a date, and (generalised beyond design.md's DueDate-only wording) is not
/// the rule's own field.
let private ensureDateFromFieldSourcesValid (rules: TemplateFieldRule list) : Result<unit, TemplateError> =
    let ruleByField = rules |> List.map (fun rule -> rule.Field, rule) |> Map.ofList

    let problem =
        rules
        |> List.tryPick (fun rule ->
            match rule.Rule with
            | DateFromField source when source = rule.Field -> Some (DerivationSourceIsSelf rule.Field)
            | DateFromField source ->
                match Map.tryFind source ruleByField with
                | None -> Some (DerivationSourceMissing source)
                | Some sourceRule when not (isDateHinted sourceRule) -> Some (DerivationSourceNotADate source)
                | Some _ -> None
            | AfterLabel _ | LinesAfterLabel _ | RegexCapture _ | FixedValue _ | SubjectCapture _ | AttachmentName _ ->
                None)

    match problem with
    | Some error -> Error error
    | None -> Ok ()

/// Per-rule checks that need no context beyond the rule itself: the date format (any rule may
/// carry an AsDate hint), the offset range (LinesAfterLabel only), and - for the three
/// pattern-carrying kinds - that the pattern compiles and has a capture group. Builds the
/// compiled-pattern map as it goes, so a template with no regex-based rules produces an empty one
/// rather than a separate pass.
let private validateRule
    (compiledSoFar: Map<TargetField, Regex>)
    (rule: TemplateFieldRule)
    : Result<Map<TargetField, Regex>, TemplateError> =
    result {
        do!
            match rule.Hint with
            | AsDate format ->
                try
                    DateTime.Now.ToString(format: string) |> ignore
                    Ok ()
                with :? FormatException as ex ->
                    Error (DateFormatInvalid(rule.Field, ex.Message))
            | AsText | AsMoney _ -> Ok ()

        match rule.Rule with
        | LinesAfterLabel(_, offset) ->
            if offset < MinimumOffset || offset > MaximumOffset then
                return! Error (OffsetOutOfRange(rule.Field, offset))
            else
                return compiledSoFar
        | RegexCapture pattern
        | SubjectCapture pattern
        | AttachmentName pattern ->
            let! regex = compilePattern pattern |> Result.mapError (fun reason -> PatternInvalid(rule.Field, reason))

            if hasCaptureGroup regex then
                return Map.add rule.Field regex compiledSoFar
            else
                return! Error (PatternHasNoCaptureGroup rule.Field)
        | AfterLabel _
        | FixedValue _
        | DateFromField _ ->
            return compiledSoFar
    }

/// The only door to ValidTemplate. Order follows design.md's sequence diagram: name, then every
/// required field has a rule, no duplicate field, each pattern compiles and has a capture group,
/// each date format is real, DateFromField sources are sound - and, ahead of all of that, the
/// supplier id itself parses. design.md's diagram never shows a "parse the supplier id" step, but
/// EditSupplierWorkflow.validate is the established precedent for this exact shape of problem
/// (an id arriving as an untrusted string): it parses the id itself rather than deferring to its
/// caller, and TemplateSupplierIdInvalid mirrors SupplierIdInvalid for the same reason.
let validateTemplate (input: UnvalidatedTemplate) : Result<ValidTemplate, TemplateError> =
    result {
        let! supplierId = SupplierId.create input.SupplierId |> Result.mapError TemplateSupplierIdInvalid
        let! name = TemplateName.create input.Name |> Result.mapError TemplateNameInvalid
        do! ensureRequiredFieldsHaveRules input.Rules
        do! ensureNoDuplicateField input.Rules
        do! ensureDateFromFieldSourcesValid input.Rules

        let! compiledPatterns =
            input.Rules
            |> List.fold
                (fun accumulated rule -> accumulated |> Result.bind (fun compiled -> validateRule compiled rule))
                (Ok Map.empty)

        return
            {
                SupplierId' = supplierId
                Name' = name
                Part' = input.Part
                Position' = input.Position
                Rules' = input.Rules
                CompiledPatterns' = compiledPatterns
            }
    }
