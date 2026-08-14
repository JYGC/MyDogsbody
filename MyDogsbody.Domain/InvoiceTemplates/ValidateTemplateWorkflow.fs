module MyDogsbody.Domain.InvoiceTemplates.ValidateTemplateWorkflow

open System
open System.Globalization
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
/// RegexParseException (itself an ArgumentException) on either engine. A NULL pattern throws
/// ArgumentNullException, which neither of those clauses names - and UnvalidatedTemplate is the
/// untrusted type, so it is guarded ahead of them rather than left to escape as an exception
/// thrown out of a domain workflow.
///
/// Whether the result used the fallback is readable off its own Options - HasFlag
/// RegexOptions.NonBacktracking - so no separate flag needs to be threaded through this result.
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

/// The fixed date every AsDate format is probed with - a constant, not DateTime.Now, because the
/// domain centre reads no clock and because a validation result that depended on today's date
/// would be untestable. Chosen so day (4), month (3), hour (13) and minute (45) are all distinct
/// from each other and from the 1/1/0001 defaults a parser substitutes for a component the format
/// never supplies.
let private dateFormatProbe = DateTime(2026, 3, 4, 13, 45, 56)

/// An AsDate format is validated with the operation it will actually be used for - parsing - not
/// merely with formatting.
///
/// DateTime.ToString rejects only two things: a lone unknown standard specifier, and an
/// unterminated quote. Every other unrecognised character is emitted as a literal, so a
/// format-only check accepts "yyyy-MM-DD" (DD is literal text), "YYYY-MM-DD", "qq" and
/// "dd/mm/yyyy" (mm is MINUTES) - each of which then silently matches nothing at scan time rather
/// than reporting anything, the failure mode tasks.md calls the most dangerous one.
///
/// Round-tripping the probe and requiring the whole calendar date back rejects all of those: a
/// format that cannot read back the day, month and year it just wrote cannot read a date off an
/// invoice either. Requiring the full date (rather than merely that parsing succeeds) is what
/// catches the literal-text cases - "yyyy-MM-DD" parses its own output happily, but comes back
/// with the day defaulted.
///
/// InvariantCulture at both ends, deliberately - ParseHint's own comment states the rule. Under
/// ambient culture the probe renders as 2569-03-04 in th-TH and 1447-09-15 in ar-SA, so whether a
/// template could be saved would otherwise depend on the machine that saved it.
let private validateDateFormat (field: TargetField) (format: string) : Result<unit, TemplateError> =
    let rendered =
        try
            Some (dateFormatProbe.ToString(format, CultureInfo.InvariantCulture))
        with :? FormatException ->
            None

    match rendered with
    | None -> Error (DateFormatInvalid(field, $"'{format}' is not a usable date format."))
    | Some text ->
        match DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, parsed when parsed.Date = dateFormatProbe.Date -> Ok ()
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
            | AsDate format -> validateDateFormat rule.Field format
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
