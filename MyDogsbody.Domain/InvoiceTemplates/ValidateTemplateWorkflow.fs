module MyDogsbody.Domain.InvoiceTemplates.ValidateTemplateWorkflow

open System
open System.Globalization
open System.Text.RegularExpressions
open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ValidateTemplateWorkflow.fs: compilePattern
let compilePattern (pattern: string) : Result<Regex, string> =
    if isNull pattern then
        Error "Pattern must not be empty."
    else
        let timeout = TimeSpan.FromMilliseconds 250.0

        // CultureInvariant pins the case-folding table IgnoreCase uses. Without it .NET folds case
        // against CultureInfo.CurrentCulture, captured when the Regex is constructed - and under
        // tr-TR / az-AZ the dotless i makes 'I' fold to 'ı' rather than 'i', so a stored pattern
        // that matches on one machine silently matches nothing on another. Bound once so both the
        // NonBacktracking construction and the fallback are guaranteed to agree.
        let caseFolding = RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant

        try
            Ok (Regex(pattern, RegexOptions.NonBacktracking ||| caseFolding, timeout))
        with
        | :? NotSupportedException ->
            try
                Ok (Regex(pattern, caseFolding, timeout))
            with :? RegexParseException as caughtException ->
                Error caughtException.Message
        | :? RegexParseException as caughtException ->
            Error caughtException.Message

let private hasCaptureGroup (regex: Regex) : bool =
    regex.GetGroupNumbers() |> Array.exists (fun number -> number > 0)

[<Literal>]
let private MinimumOffset = 0

[<Literal>]
let private MaximumOffset = 20

let private requiredFields = [ Reference; Amount; Currency ]

/// A constant, not DateTime.Now, because the domain centre reads no clock and because a
/// validation result that depended on today's date would be untestable. Chosen so day (4), month
/// (3), hour (13) and minute (45) are all distinct from each other and from the 1/1/0001 defaults
/// a parser substitutes for a component the format never supplies.
let private fixedDateEveryAsDateFormatIsProbedWith = DateTime(2026, 3, 4, 13, 45, 56)

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ValidateTemplateWorkflow.fs: validateAsDateFormatByParsingBackTheDateItWritesNotMerelyByFormatting
let private validateAsDateFormatByParsingBackTheDateItWritesNotMerelyByFormatting
    (field: TargetField)
    (format: string)
    : Result<unit, TemplateError> =
    let rendered =
        try
            Some (fixedDateEveryAsDateFormatIsProbedWith.ToString(format, CultureInfo.InvariantCulture))
        with :? FormatException ->
            None

    match rendered with
    | None -> Error (DateFormatInvalid(field, $"'{format}' is not a usable date format."))
    | Some text ->
        match DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, parsed when parsed.Date = fixedDateEveryAsDateFormatIsProbedWith.Date -> Ok ()
        | _ -> Error (DateFormatInvalid(field, $"'{format}' cannot read back the date it writes ('{text}')."))

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

/// What a DateFromField source must be, or the derivation could never produce a date to add the
/// payment term to.
let private fieldsOwnRuleReadsAsADate (rule: TemplateFieldRule) : bool =
    match rule.Hint with
    | AsDate _ -> true
    | AsText | AsMoney _ -> false

/// Cross-rule checks - these need the whole rule set as context, unlike the per-rule checks in
/// validateOneRuleOnItsOwnAndAddItsCompiledPatternToTheMap. Checked in the order design.md's
/// sequence diagram states, generalised beyond design.md's DueDate-only wording.
let private ensureEveryDateFromFieldSourceExistsIsADateAndIsNotTheRulesOwnField
    (rules: TemplateFieldRule list)
    : Result<unit, TemplateError> =
    let ruleByField = rules |> List.map (fun rule -> rule.Field, rule) |> Map.ofList

    let problem =
        rules
        |> List.tryPick (fun rule ->
            match rule.Rule with
            | DateFromField source when source = rule.Field -> Some (DerivationSourceIsSelf rule.Field)
            | DateFromField source ->
                match Map.tryFind source ruleByField with
                | None -> Some (DerivationSourceMissing source)
                | Some sourceRule when not (fieldsOwnRuleReadsAsADate sourceRule) ->
                    Some (DerivationSourceNotADate source)
                | Some _ -> None
            | AfterLabel _ | LinesAfterLabel _ | RegexCapture _ | FixedValue _ | SubjectCapture _ | AttachmentName _ ->
                None)

    match problem with
    | Some error -> Error error
    | None -> Ok ()

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ValidateTemplateWorkflow.fs: ensureEveryDerivationIsDueDateFromIssueDateTheOnlyOneTheEnginePerforms
let private ensureEveryDerivationIsDueDateFromIssueDateTheOnlyOneTheEnginePerforms
    (rules: TemplateFieldRule list)
    : Result<unit, TemplateError> =
    let unsupported =
        rules
        |> List.tryPick (fun rule ->
            match rule.Rule with
            | DateFromField source when not (rule.Field = DueDate && source = IssueDate) ->
                Some (DerivationUnsupported(rule.Field, source))
            | AfterLabel _ | LinesAfterLabel _ | RegexCapture _ | FixedValue _ | SubjectCapture _ | AttachmentName _
            | DateFromField _ -> None)

    match unsupported with
    | Some error -> Error error
    | None -> Ok ()

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ValidateTemplateWorkflow.fs: ensureHintsMatchFields
let private ensureHintsMatchFields (rules: TemplateFieldRule list) : Result<unit, TemplateError> =
    let readsText (rule: TemplateFieldRule) =
        match rule.Rule with
        | DateFromField _ -> false
        | AfterLabel _ | LinesAfterLabel _ | RegexCapture _ | FixedValue _ | SubjectCapture _ | AttachmentName _ -> true

    let mismatch =
        rules
        |> List.tryPick (fun rule ->
            match rule.Field, rule.Hint with
            | Amount, AsMoney _ -> None
            | Amount, (AsText | AsDate _) -> Some (FieldHintMismatch(rule.Field, rule.Hint))
            | (IssueDate | DueDate), AsDate _ -> None
            | (IssueDate | DueDate), (AsText | AsMoney _) when readsText rule ->
                Some (FieldHintMismatch(rule.Field, rule.Hint))
            | _ -> None)

    match mismatch with
    | Some error -> Error error
    | None -> Ok ()

/// A rule that reads a part the template never selects can never match, so the field it fills is
/// silently absent on every message forever - the scan-time silence this whole validation boundary
/// exists to convert into a save-time refusal.
///
/// Exactly one pairing is contradictory today: AttachmentName on a Body-scoped template, whose
/// selected content carries no filenames by construction. Not SubjectCapture - the subject is
/// available whichever part a template selects - and not the text-reading kinds, which read
/// whichever part was selected. AnyPart and Attachment both put filenames in front of the rule, so
/// only Body is refused.
let private ensureRulesCanReachTheirPart (part: DocumentPart) (rules: TemplateFieldRule list) : Result<unit, TemplateError> =
    let unreachable =
        match part with
        | Body ->
            rules
            |> List.tryPick (fun rule ->
                match rule.Rule with
                | AttachmentName _ -> Some (RuleUnreachableForPart(rule.Field, part))
                | AfterLabel _ | LinesAfterLabel _ | RegexCapture _ | FixedValue _ | SubjectCapture _ | DateFromField _ ->
                    None)
        | Attachment _
        | AnyPart -> None

    match unreachable with
    | Some error -> Error error
    | None -> Ok ()

/// The label check is IsNullOrWhiteSpace rather than an emptiness test, and it is the most
/// damaging of the save-time gaps this file closes. "".IndexOf returns 0 against any string, so
/// AfterLabel "" matched the first line of every document and returned the whole of it, and
/// LinesAfterLabel("", 1) returned the second - both then passed the engine's own empty check and
/// stored a confidently wrong value rather than dropping a field. A whitespace-only label does the
/// same on the first line carrying a space. Null counts too: a label arrives from a stored row,
/// and a NULL column is how one reaches the domain - where String.IndexOf(null, StringComparison)
/// would raise out of a pure workflow.
let private validateOneRuleOnItsOwnAndAddItsCompiledPatternToTheMap
    (compiledSoFar: Map<TargetField, Regex>)
    (rule: TemplateFieldRule)
    : Result<Map<TargetField, Regex>, TemplateError> =
    result {
        do!
            match rule.Hint with
            | AsDate format -> validateAsDateFormatByParsingBackTheDateItWritesNotMerelyByFormatting rule.Field format
            | AsText | AsMoney _ -> Ok ()

        match rule.Rule with
        | AfterLabel label ->
            if String.IsNullOrWhiteSpace label then
                return! Error (LabelIsEmpty rule.Field)
            else
                return compiledSoFar
        | LinesAfterLabel(label, offset) ->
            if String.IsNullOrWhiteSpace label then
                return! Error (LabelIsEmpty rule.Field)
            elif offset < MinimumOffset || offset > MaximumOffset then
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
        | FixedValue _
        | DateFromField _ ->
            return compiledSoFar
    }

/// The only door to ValidTemplate. Order follows design.md's sequence diagram: name, then every
/// required field has a rule, no duplicate field, each pattern compiles and has a capture group,
/// each label is a label, each date format is real, DateFromField sources are sound, no rule reads
/// a part the template never selects - and, ahead of all of that, the supplier id itself parses. design.md's diagram never shows a "parse the supplier id" step, but
/// EditSupplierWorkflow.validate is the established precedent for this exact shape of problem
/// (an id arriving as an untrusted string): it parses the id itself rather than deferring to its
/// caller, and TemplateSupplierIdInvalid mirrors SupplierIdInvalid for the same reason.
let validateTemplate (input: UnvalidatedTemplate) : Result<ValidTemplate, TemplateError> =
    result {
        let! supplierId = SupplierId.create input.SupplierId |> Result.mapError TemplateSupplierIdInvalid
        let! name = TemplateName.create input.Name |> Result.mapError TemplateNameInvalid
        do! ensureRequiredFieldsHaveRules input.Rules
        do! ensureNoDuplicateField input.Rules
        do! ensureEveryDateFromFieldSourceExistsIsADateAndIsNotTheRulesOwnField input.Rules
        do! ensureEveryDerivationIsDueDateFromIssueDateTheOnlyOneTheEnginePerforms input.Rules
        do! ensureHintsMatchFields input.Rules
        do! ensureRulesCanReachTheirPart input.Part input.Rules

        let! compiledPatterns =
            input.Rules
            |> List.fold
                (fun accumulated rule ->
                    accumulated
                    |> Result.bind (fun compiled ->
                        validateOneRuleOnItsOwnAndAddItsCompiledPatternToTheMap compiled rule))
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

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ValidateTemplateWorkflow.fs: reconstructValidTemplate
let reconstructValidTemplate
    (supplierId: SupplierId)
    (name: TemplateName)
    (part: DocumentPart)
    (position: int)
    (rules: TemplateFieldRule list)
    : Result<ValidTemplate, string> =
    let compilePatternFor (rule: TemplateFieldRule) (compiled: Map<TargetField, Regex>) : Result<Map<TargetField, Regex>, string> =
        match rule.Rule with
        | RegexCapture pattern
        | SubjectCapture pattern
        | AttachmentName pattern -> compilePattern pattern |> Result.map (fun regex -> Map.add rule.Field regex compiled)
        | AfterLabel _
        | LinesAfterLabel _
        | FixedValue _
        | DateFromField _ -> Ok compiled

    rules
    |> List.fold (fun accumulated rule -> accumulated |> Result.bind (compilePatternFor rule)) (Ok Map.empty)
    |> Result.map (fun compiledPatterns ->
        {
            SupplierId' = supplierId
            Name' = name
            Part' = part
            Position' = position
            Rules' = rules
            CompiledPatterns' = compiledPatterns
        })
