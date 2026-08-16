module MyDogsbody.Domain.Invoices.ApplyTemplateWorkflow

open System
open System.Globalization
open System.Text.RegularExpressions
open MyDogsbody.Domain
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

/// The normalized text lines and attachment filenames a template's DocumentPart selects, plus
/// the subject - always available, since SubjectCapture reads it regardless of DocumentPart.
/// Each selected part's lines are normalized on their own before being flattened together, so a
/// wrapped-continuation join (TextNormalization's within-block rule) never happens ACROSS two
/// different parts - they are different documents, and their BlockIndex numbering is unrelated.
type private SelectedContent =
    { Lines: TextLine list
      AttachmentNames: string list
      Subject: string }

let private partMatchesSelector (selector: DocumentPart) (part: MessagePart) : bool =
    match selector, part with
    | AnyPart, _ -> true
    | Body, BodyPart -> true
    | Attachment wantedFormat, AttachmentPart(_, format) -> format = wantedFormat
    | Body, (AttachmentPart _ | SubjectPart)
    | Attachment _, (BodyPart | SubjectPart) -> false

let private selectContent (part: DocumentPart) (message: ScannedMessage) : SelectedContent =
    let selectedParts = message.Parts |> List.filter (fun (messagePart, _) -> partMatchesSelector part messagePart)

    {
        Lines = selectedParts |> List.collect (fun (_, lines) -> TextNormalization.normalize lines)
        AttachmentNames =
            selectedParts
            |> List.choose (fun (messagePart, _) ->
                match messagePart with
                | AttachmentPart(name, _) -> Some name
                | BodyPart | SubjectPart -> None)
        Subject = message.Subject
    }

/// A rule either finds a string, finds nothing, or times out - the three ways a rule can fail to
/// hand back a value, none of them by raising. RegexMatchTimeoutException is caught right here so
/// nothing above this line ever needs to know a Regex is involved.
type private RuleOutcome =
    | Found of string
    | NotFound
    | TimedOut

/// An extraction that produced nothing but whitespace is not a value - it is the rule failing to
/// find one, and saying so is what keeps an empty Reference (the natural key change #4 builds its
/// ledger rows and calendar events on) out of ExtractedInvoice. Applied once, over every rule
/// kind's outcome, rather than per kind.
let private emptyIsNotFound (outcome: RuleOutcome) : RuleOutcome =
    match outcome with
    | Found value when String.IsNullOrWhiteSpace value -> NotFound
    | Found _
    | NotFound
    | TimedOut -> outcome

let private runRegexOnce (regex: Regex) (input: string) : RuleOutcome =
    try
        let regexMatch = regex.Match input

        // Groups.[1].Success as well as the match's own: an optional group that never
        // participated (INV-(\d+)? against "INV-") reports Value = "" off a successful match,
        // which is "the pattern matched but captured nothing", not a captured empty string.
        if regexMatch.Success && regexMatch.Groups.Count > 1 && regexMatch.Groups.[1].Success then
            Found regexMatch.Groups.[1].Value
        else
            NotFound
    with :? RegexMatchTimeoutException ->
        TimedOut

/// Tries a compiled pattern against each candidate in turn, stopping at the first Found - or the
/// first TimedOut, which is treated as a stop rather than retried against later candidates:
/// timing out once on a pathological pattern is already the signal that pattern is dangerous, not
/// a reason to spend the timeout budget again on the next line or filename.
///
/// Seq, not List: List.map is eager, so the version this replaced ran the pattern against every
/// candidate before picking the first outcome - making the stop above a comment rather than a
/// behaviour, and spending compilePattern's 250ms budget once per line instead of once. The
/// outcome chosen is unchanged; only the candidates never reached are.
let private runRegexAcross (regex: Regex) (candidates: string list) : RuleOutcome =
    candidates
    |> Seq.map (runRegexOnce regex)
    |> Seq.tryFind (function
        | Found _
        | TimedOut -> true
        | NotFound -> false)
    |> Option.defaultValue NotFound

let private tryFindLabelledLine (label: string) (lines: TextLine list) : (TextLine * int) option =
    lines
    |> List.indexed
    |> List.tryFind (fun (_, candidateLine) -> candidateLine.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
    |> Option.map (fun (index, candidateLine) -> candidateLine, index)

let private runRule
    (compiledPatterns: Map<TargetField, Regex>)
    (field: TargetField)
    (rule: FieldRule)
    (content: SelectedContent)
    : RuleOutcome =
    emptyIsNotFound (
    match rule with
    | AfterLabel label ->
        match tryFindLabelledLine label content.Lines with
        | Some (matchedLine, _) ->
            let labelIndex = matchedLine.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase)
            Found (matchedLine.Text.Substring(labelIndex + label.Length).Trim())
        | None -> NotFound
    | LinesAfterLabel(label, offset) ->
        match tryFindLabelledLine label content.Lines with
        | Some (_, labelIndex) ->
            let targetIndex = labelIndex + offset

            if targetIndex >= 0 && targetIndex < content.Lines.Length then
                Found content.Lines.[targetIndex].Text
            else
                NotFound
        | None -> NotFound
    | RegexCapture _ -> runRegexAcross (Map.find field compiledPatterns) (content.Lines |> List.map (fun l -> l.Text))
    | FixedValue value -> Found value
    | SubjectCapture _ -> runRegexOnce (Map.find field compiledPatterns) content.Subject
    | AttachmentName _ -> runRegexAcross (Map.find field compiledPatterns) content.AttachmentNames
    | DateFromField _ -> NotFound // handled separately in applyTemplate - this rule never reads text
    )

/// task 4.6: the same reference printed "1234 5678 90" in a PDF and "1234567890" in an
/// attachment filename must produce one value, or the natural key later turns one invoice into
/// two ledger rows and two calendar events. Folded here, where the two sources first meet;
/// change #4's InvoiceReference.create reuses this rather than duplicating it.
let foldReferenceWhitespace (raw: string) : string =
    raw |> String.filter (fun c -> not (Char.IsWhiteSpace c))

/// Strips everything but digits, the stated decimal separator and a leading minus sign - which
/// silently handles currency symbols, thousands separators and a trailing CR/DR suffix all at
/// once, since none of those characters survive the filter.
let private parseAmount (decimalSeparator: char) (raw: string) : decimal option =
    let significant =
        raw
        |> String.filter (fun c -> Char.IsDigit c || c = decimalSeparator || c = '-')
        |> fun s -> if decimalSeparator <> '.' then s.Replace(decimalSeparator, '.') else s

    match Decimal.TryParse(significant, NumberStyles.AllowDecimalPoint ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
    | true, value -> Some value
    | false, _ -> None

/// Explicit format, InvariantCulture, TryParseExact - never ambient-culture DateTime.Parse. This
/// is what makes 02/08/2016 read with d/M/yyyy 2 August and the same text read with M/d/yyyy 8
/// February, deterministically, regardless of the machine's locale.
let private parseDate (format: string) (raw: string) : DateTime option =
    match DateTime.TryParseExact(raw.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, value -> Some value
    | false, _ -> None

/// Reads the decimal separator out of the hint if it is AsMoney, defaulting to '.' otherwise.
/// Every measured template pairs Amount with AsMoney; this default exists only so a
/// mismatched hint (never validated against, never produced by the editor) fails to parse
/// rather than crashing the engine.
let private extractMoney (field: TargetField) (templateId: TemplateId) (hint: ParseHint) (raw: string) : Result<decimal, InvoiceError> =
    let decimalSeparator = match hint with
                            | AsMoney separator -> separator
                            | AsText
                            | AsDate _ -> '.'

    match parseAmount decimalSeparator raw with
    | Some value -> Ok value
    | None -> Error (AmountUnparseable(field, raw))

/// Same defensive-default reasoning as extractMoney, for the date-format string.
let private extractDate (field: TargetField) (templateId: TemplateId) (hint: ParseHint) (raw: string) : Result<DateTime, InvoiceError> =
    let format = match hint with
                 | AsDate dateFormat -> dateFormat
                 | AsText
                 | AsMoney _ -> ""

    match parseDate format raw with
    | Some value -> Ok value
    | None -> Error (DateUnparseable(field, raw, format))

/// Applies one template to one message: runs each field rule against the normalized text the
/// template's DocumentPart selects, parses each result with that rule's hint, and derives DueDate
/// from IssueDate when it names DateFromField. Pure - no I/O, no clock, no randomness, and no
/// dependency parameters; PaymentTermDays, TemplateId and ScannedMessage are plain input data,
/// not dependency function types.
///
/// TemplateId is not part of design.md's listed signature for this function, but ExtractedInvoice
/// and InvoiceError's template-carrying cases both need one and ValidTemplate itself carries
/// none - a gap in the documented signature, closed here rather than deferred to a caller that
/// would otherwise have to reconstruct these values after the fact.
///
/// Fields are evaluated in a fixed order - Reference, Amount, Currency, IssueDate, DueDate - so
/// that DueDate's DateFromField can read an already-computed IssueDate. This is not a general
/// dependency solver: every measured template (and everything DerivationSourceIsSelf and
/// DerivationSourceNotADate allow through validation) only ever derives DueDate from IssueDate,
/// so nothing here needs to handle a forward reference or a longer chain.
let applyTemplate
    (paymentTerm: PaymentTermDays)
    (templateId: TemplateId)
    (template: ValidTemplate)
    (message: ScannedMessage)
    : Result<ExtractedInvoice, InvoiceError> =
    let rules = ValidTemplate.rules template
    let content = selectContent (ValidTemplate.part template) message
    let compiledPatterns = ValidTemplate.compiledPatterns template
    let findRule field = rules |> List.tryFind (fun rule -> rule.Field = field)

    // Reference and Amount always have exactly one rule - ValidateTemplateWorkflow refuses to
    // produce a ValidTemplate missing either, so this can never actually raise. Currency is not
    // required (see ExtractedInvoice's doc comment) and is read via findRule below instead.
    let requiredRule field =
        match findRule field with
        | Some rule -> rule
        | None -> failwith $"invariant violated: required field {field} has no rule on a validated template"

    result {
        let! reference =
            let rule = requiredRule Reference

            match runRule compiledPatterns Reference rule.Rule content with
            | Found raw -> Ok (foldReferenceWhitespace raw)
            | NotFound -> Error (TemplateMatchedNothing(templateId, Reference))
            | TimedOut -> Error (RuleTimedOut(templateId, Reference))

        let! amount =
            let rule = requiredRule Amount

            match runRule compiledPatterns Amount rule.Rule content with
            | Found raw -> extractMoney Amount templateId rule.Hint raw
            | NotFound -> Error (TemplateMatchedNothing(templateId, Amount))
            | TimedOut -> Error (RuleTimedOut(templateId, Amount))

        // Currency is optional: no rule, "found nothing" and a timeout all yield None rather
        // than an error, the same treatment IssueDate/DueDate get below. Currency has no parse
        // step of its own (the raw match IS the value), so unlike a date or an amount there is
        // no "found but unparseable" case to report either.
        let currency =
            match findRule Currency with
            | None -> None
            | Some rule ->
                match runRule compiledPatterns Currency rule.Rule content with
                | Found raw -> Some raw
                | NotFound
                | TimedOut -> None

        // IssueDate and DueDate are optional: no rule, "found nothing" and a timeout all yield
        // None rather than an error - only a rule that DID find text but could not parse it is an
        // error, for either field, matching requirements.md's "never ... a silently dropped
        // field" for values a rule actually produced.
        let! issueDate =
            match findRule IssueDate with
            | None -> Ok None
            | Some rule ->
                match rule.Rule with
                | DateFromField _ -> Ok None // IssueDate deriving from DueDate: out of scope, see doc comment above
                | _ ->
                    match runRule compiledPatterns IssueDate rule.Rule content with
                    | Found raw -> extractDate IssueDate templateId rule.Hint raw |> Result.map Some
                    | NotFound
                    | TimedOut -> Ok None

        let! dueDate =
            match findRule DueDate with
            | None -> Ok None
            | Some rule ->
                match rule.Rule with
                | DateFromField source ->
                    // Pure arithmetic over an already-computed value - never fails, so this
                    // branch has no Error case of its own.
                    let sourceValue = if source = IssueDate then issueDate else None
                    Ok (sourceValue |> Option.map (fun date -> date.AddDays(float (PaymentTermDays.value paymentTerm))))
                | _ ->
                    match runRule compiledPatterns DueDate rule.Rule content with
                    | Found raw -> extractDate DueDate templateId rule.Hint raw |> Result.map Some
                    | NotFound
                    | TimedOut -> Ok None

        return
            {
                SupplierId = ValidTemplate.supplierId template
                TemplateId = templateId
                SourceMessageId = message.SourceMessageId
                Reference = reference
                Amount = amount
                Currency = currency
                IssueDate = issueDate
                DueDate = dueDate
            }
    }
