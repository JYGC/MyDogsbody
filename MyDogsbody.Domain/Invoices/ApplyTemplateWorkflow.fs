module MyDogsbody.Domain.Invoices.ApplyTemplateWorkflow

open System
open System.Globalization
open System.Text.RegularExpressions
open MyDogsbody.Domain
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

/// The text a template's DocumentPart selects, plus the subject - always available, since
/// SubjectCapture reads it regardless of DocumentPart. Already normalized: it arrives that way
/// on the NormalizedMessage.
///
/// Lines are kept GROUPED BY PART rather than flattened into one list. LinesAfterLabel is why:
/// its offset must not step out of the part the label was found in. content.Lines used to be a
/// List.collect over every selected part, so a label on the last line of cover-note.pdf with an
/// offset of 1 returned the first line of the NEXT attachment - a different document whose
/// BlockIndex numbering is unrelated.
type private SelectedContent =
    { LinesByPart: TextLine list list
      AttachmentNames: string list
      Subject: string }

let private partMatchesSelector (selector: DocumentPart) (part: MessagePart) : bool =
    match selector, part with
    | AnyPart, _ -> true
    | Body, BodyPart -> true
    | Attachment wantedFormat, AttachmentPart(_, format) -> format = wantedFormat
    | Body, (AttachmentPart _ | SubjectPart)
    | Attachment _, (BodyPart | SubjectPart) -> false

/// Filtering only - no normalization. That happened once for the whole message before any
/// template was tried, which is what stops a supplier with N templates paying for NFKC over
/// every line of every attachment N times.
let private selectContent (part: DocumentPart) (message: NormalizedMessage) : SelectedContent =
    let selectedParts =
        NormalizedMessage.parts message
        |> List.filter (fun (messagePart, _) -> partMatchesSelector part messagePart)

    {
        LinesByPart = selectedParts |> List.map snd
        AttachmentNames =
            selectedParts
            |> List.choose (fun (messagePart, _) ->
                match messagePart with
                | AttachmentPart(name, _) -> Some name
                | BodyPart | SubjectPart -> None)
        Subject = NormalizedMessage.subject message
    }

/// A rule either finds a string, finds nothing, or times out - the three ways a rule can fail to
/// hand back a value, none of them by raising. RegexMatchTimeoutException is caught right here so
/// nothing above this line ever needs to know a Regex is involved.
type private RuleOutcome =
    | Found of string
    | NotFound
    | TimedOut

/// requirements.md: "WHEN a rule finds nothing THE SYSTEM SHALL report which field and which rule
/// found nothing, never a default or an empty value silently substituted."
///
/// An extraction that came back empty IS a rule finding nothing, so every outcome is built
/// through here rather than through Found directly. Three paths used to report Found "": an
/// AfterLabel on a label-only line (the bare "Reference" line the LinesAfterLabel rules exist
/// for), a successful match whose capture group did not participate, and a FixedValue of "".
/// For Reference and Currency that empty value went straight into ExtractedInvoice - and an empty
/// reference collides in change #4's natural key, turning every such invoice into one ledger row.
let private foundUnlessEmpty (text: string) : RuleOutcome =
    if String.IsNullOrWhiteSpace text then NotFound else Found text

let private runRegexOnce (regex: Regex) (input: string) : RuleOutcome =
    // Regex.Match null throws ArgumentNullException, which the timeout clause below does not
    // name - and every input here is a string an outer-ring adapter filled in.
    if isNull input then
        NotFound
    else
        try
            let regexMatch = regex.Match input

            if regexMatch.Success && regexMatch.Groups.Count > 1 then
                foundUnlessEmpty regexMatch.Groups.[1].Value
            else
                NotFound
        with :? RegexMatchTimeoutException ->
            TimedOut

/// Tries a compiled pattern against each candidate in turn, stopping at the first Found - or the
/// first TimedOut, which is treated as a stop rather than retried against later candidates:
/// timing out once on a pathological pattern is already the signal that pattern is dangerous, not
/// a reason to spend the timeout budget again on the next line or filename.
///
/// tryPick, not map-then-tryFind: List.map is eager, so the short-circuit this comment describes
/// did not happen. With the 250ms match timeout a pathological pattern cost 250ms x lines - a
/// 200-line PDF blocked for ~50 seconds, and selectTemplate then repeated that per candidate
/// template. requirements.md: "WHEN a rule times out THE SYSTEM SHALL NOT block the user
/// interface."
let private runRegexAcross (regex: Regex) (candidates: string list) : RuleOutcome =
    candidates
    |> List.tryPick (fun candidate ->
        match runRegexOnce regex candidate with
        | NotFound -> None
        | Found _
        | TimedOut as outcome -> Some outcome)
    |> Option.defaultValue NotFound

let private lineCarriesLabel (label: string) (line: TextLine) : bool =
    line.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0

/// The first line carrying the label, searched part by part in order - the same line a flattened
/// search would have found, returned together with the part it belongs to so an offset can be
/// applied inside that part rather than across the whole message.
let private tryFindLabelledLine (label: string) (linesByPart: TextLine list list) : (TextLine list * int) option =
    linesByPart
    |> List.tryPick (fun partLines ->
        partLines
        |> List.tryFindIndex (lineCarriesLabel label)
        |> Option.map (fun index -> partLines, index))

let private runRule
    (compiledPatterns: Map<TargetField, Regex>)
    (field: TargetField)
    (rule: FieldRule)
    (content: SelectedContent)
    : RuleOutcome =
    match rule with
    | AfterLabel label ->
        match tryFindLabelledLine label content.LinesByPart with
        | Some (partLines, index) ->
            let matchedLine = List.item index partLines
            let labelIndex = matchedLine.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase)
            foundUnlessEmpty (matchedLine.Text.Substring(labelIndex + label.Length).Trim())
        | None -> NotFound
    | LinesAfterLabel(label, offset) ->
        match tryFindLabelledLine label content.LinesByPart with
        | Some (partLines, labelIndex) ->
            let targetIndex = labelIndex + offset

            if targetIndex >= 0 && targetIndex < partLines.Length then
                let target = List.item targetIndex partLines

                // requirements.md: "WHEN LinesAfterLabel is given an offset that runs past the
                // end of the BLOCK THE SYSTEM SHALL report that the rule found nothing." A label
                // on the last line of a table cell must not read the first line of the next one,
                // which is the whole reason TextLine carries a BlockIndex.
                if target.BlockIndex = (List.item labelIndex partLines).BlockIndex then
                    foundUnlessEmpty target.Text
                else
                    NotFound
            else
                NotFound
        | None -> NotFound
    | RegexCapture _ ->
        let allLines = content.LinesByPart |> List.collect (List.map (fun candidate -> candidate.Text))
        runRegexAcross (Map.find field compiledPatterns) allLines
    | FixedValue value -> foundUnlessEmpty value
    | SubjectCapture _ -> runRegexOnce (Map.find field compiledPatterns) content.Subject
    | AttachmentName _ -> runRegexAcross (Map.find field compiledPatterns) content.AttachmentNames
    | DateFromField _ -> NotFound // handled separately in applyTemplate - this rule never reads text

/// The other of '.' and ',': whichever character the template did NOT call its decimal separator
/// is the one its documents use to group thousands.
let private thousandsSeparatorFor (decimalSeparator: char) : char =
    if decimalSeparator = ',' then '.' else ','

/// Every maximal run of number-shaped characters in the text, in order.
///
/// Splitting into runs is what makes "Total for INV-1042: $245.00" two candidates rather than one
/// number. The previous implementation kept every digit, every '-' and the separator with a
/// global String.filter and parsed the concatenation, so - measured - that line booked
/// -1042245.00, "245.00 due 14/07/2026" booked 245.0014072026, and "Ref 2 items $10.50" booked
/// 210.50. All three silently, with no AmountUnparseable and nothing to notice them by.
let private numericRuns (decimalSeparator: char) (raw: string) : string list =
    let thousandsSeparator = thousandsSeparatorFor decimalSeparator
    let isNumberShaped c = Char.IsDigit c || c = decimalSeparator || c = thousandsSeparator || c = '-'
    let asRun (chars: char list) = String(chars |> List.rev |> List.toArray)

    let completed, trailing =
        (([], []), raw)
        ||> Seq.fold (fun (completed, current) character ->
            if isNumberShaped character then completed, character :: current
            elif List.isEmpty current then completed, []
            else asRun current :: completed, [])

    (if List.isEmpty trailing then completed else asRun trailing :: completed) |> List.rev

/// One number out of the text, or nothing. Currency symbols, thousands separators, a trailing
/// CR/DR suffix and a full stop ending the sentence all fall away; a SECOND number anywhere in
/// the text does not. Two candidates is an ambiguity this reports rather than resolves - guessing
/// puts a wrong amount in the ledger with nothing to notice it by, which is the failure
/// requirements.md's "never a default ... silently substituted" is written against.
///
/// Known limitation, stated rather than hidden: a document that groups thousands with a SPACE
/// ("1 234,56") reads as two candidates and is refused. That is a reported AmountUnparseable the
/// user can answer with a RegexCapture rule, not a wrong number - which is what the old filter
/// produced for the same input.
let private parseAmount (decimalSeparator: char) (raw: string) : decimal option =
    let thousandsSeparator = thousandsSeparatorFor decimalSeparator

    let candidates =
        numericRuns decimalSeparator raw
        // A run can END on a separator that was really punctuation - "$245.00." - but never
        // STARTS on one that was, since ".50" is a legitimate way to write half a unit.
        |> List.map (fun run -> run.TrimEnd(decimalSeparator, thousandsSeparator))
        |> List.filter (Seq.exists Char.IsDigit)

    match candidates with
    | [ single ] ->
        let withoutGrouping = single.Replace(string thousandsSeparator, "")

        let normalized =
            if decimalSeparator <> '.' then withoutGrouping.Replace(decimalSeparator, '.') else withoutGrouping

        match
            Decimal.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint ||| NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture
            )
        with
        | true, value -> Some value
        | false, _ -> None
    | _ -> None

/// Explicit format, InvariantCulture, TryParseExact - never ambient-culture DateTime.Parse. This
/// is what makes 02/08/2016 read with d/M/yyyy 2 August and the same text read with M/d/yyyy 8
/// February, deterministically, regardless of the machine's locale.
let private parseDate (format: string) (raw: string) : DateTime option =
    match DateTime.TryParseExact(raw.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, value -> Some value
    | false, _ -> None

/// Reads the decimal separator out of the hint. The non-AsMoney branch is unreachable:
/// ValidateTemplateWorkflow refuses to produce a ValidTemplate whose Amount rule is not
/// AsMoney-hinted, which is the save-time refusal that replaced this defaulting to '.' and
/// parsing money out of a rule the user had hinted as text.
let private extractMoney (field: TargetField) (hint: ParseHint) (raw: string) : Result<decimal, InvoiceError> =
    let decimalSeparator =
        match hint with
        | AsMoney separator -> separator
        | AsText
        | AsDate _ -> '.'

    match parseAmount decimalSeparator raw with
    | Some value -> Ok value
    | None -> Error (AmountUnparseable(field, raw))

/// Same reasoning as extractMoney, for the date-format string: a date field whose rule reads text
/// must carry AsDate, so the "" branch is unreachable. It used to be reachable, and every message
/// then failed the WHOLE extraction with DateUnparseable(field, raw, "") - an error quoting an
/// empty format string, from a template that had saved without complaint.
let private extractDate (field: TargetField) (hint: ParseHint) (raw: string) : Result<DateTime, InvoiceError> =
    let format =
        match hint with
        | AsDate dateFormat -> dateFormat
        | AsText
        | AsMoney _ -> ""

    match parseDate format raw with
    | Some value -> Ok value
    | None -> Error (DateUnparseable(field, raw, format))

/// Applies one template to one message: runs each field rule against the normalized text the
/// template's DocumentPart selects, parses each result with that rule's hint, and derives DueDate
/// from IssueDate when it names DateFromField. Pure - no I/O, no clock, no randomness, and no
/// dependency parameters; PaymentTermDays, TemplateId and NormalizedMessage are plain input data,
/// not dependency function types.
///
/// TemplateId is not part of design.md's listed signature for this function, but ExtractedInvoice
/// and InvoiceError's template-carrying cases both need one and ValidTemplate itself carries
/// none - a gap in the documented signature, closed here rather than deferred to a caller that
/// would otherwise have to reconstruct these values after the fact.
///
/// The input is a NormalizedMessage rather than a ScannedMessage so that normalization happens
/// once per message, above selectTemplate's loop, instead of once per candidate template inside
/// it. Callers reach it through MessageNormalization.normalizeMessage.
///
/// Fields are evaluated in a fixed order - Reference, Amount, Currency, IssueDate, DueDate - so
/// that DueDate's DateFromField can read an already-computed IssueDate. This is not a general
/// dependency solver, and it no longer pretends to be one by returning None for the pairings it
/// cannot handle: ValidateTemplateWorkflow refuses at save time every derivation except DueDate
/// from IssueDate, so a forward reference or a longer chain cannot reach this function.
let applyTemplate
    (paymentTerm: PaymentTermDays)
    (templateId: TemplateId)
    (template: ValidTemplate)
    (message: NormalizedMessage)
    : Result<ExtractedInvoice, InvoiceError> =
    let rules = ValidTemplate.rules template
    let content = selectContent (ValidTemplate.part template) message
    let compiledPatterns = ValidTemplate.compiledPatterns template
    let findRule field = rules |> List.tryFind (fun rule -> rule.Field = field)

    // Reference, Amount and Currency always have exactly one rule - ValidateTemplateWorkflow
    // refuses to produce a ValidTemplate missing one, so this can never actually raise.
    let requiredRule field =
        match findRule field with
        | Some rule -> rule
        | None -> failwith $"invariant violated: required field {field} has no rule on a validated template"

    result {
        let! reference =
            let rule = requiredRule Reference

            match runRule compiledPatterns Reference rule.Rule content with
            | Found raw -> Ok (InvoiceText.foldReferenceWhitespace raw)
            | NotFound -> Error (TemplateMatchedNothing(templateId, Reference))
            | TimedOut -> Error (RuleTimedOut(templateId, Reference))

        let! amount =
            let rule = requiredRule Amount

            match runRule compiledPatterns Amount rule.Rule content with
            | Found raw -> extractMoney Amount rule.Hint raw
            | NotFound -> Error (TemplateMatchedNothing(templateId, Amount))
            | TimedOut -> Error (RuleTimedOut(templateId, Amount))

        let! currency =
            let rule = requiredRule Currency

            match runRule compiledPatterns Currency rule.Rule content with
            | Found raw -> Ok raw
            | NotFound -> Error (TemplateMatchedNothing(templateId, Currency))
            | TimedOut -> Error (RuleTimedOut(templateId, Currency))

        // IssueDate and DueDate are optional: having no rule at all, and having a rule that finds
        // nothing, both yield None rather than an error.
        //
        // A TIMEOUT is not one of those. requirements.md -> Regex safety asks for two separate
        // things - "fail THAT rule with RuleTimedOut naming the field and the template" and
        // "allow the rest of the scan to finish" - and reporting the field as merely absent
        // satisfies neither. A user whose IssueDate pattern backtracks catastrophically would
        // otherwise burn the timeout budget on every scan, get no due date, and be told nothing
        // at all: the exact silence the 12% -> 39% derivation exists to prevent.
        let! issueDate =
            match findRule IssueDate with
            | None -> Ok None
            | Some rule ->
                match rule.Rule with
                | DateFromField _ ->
                    // Unreachable: validation refuses every derivation but DueDate from IssueDate.
                    Ok None
                | _ ->
                    match runRule compiledPatterns IssueDate rule.Rule content with
                    | Found raw -> extractDate IssueDate rule.Hint raw |> Result.map Some
                    | NotFound -> Ok None
                    | TimedOut -> Error (RuleTimedOut(templateId, IssueDate))

        let! dueDate =
            match findRule DueDate with
            | None -> Ok None
            | Some rule ->
                match rule.Rule with
                | DateFromField _ ->
                    // Pure arithmetic over an already-computed value - never fails, so this
                    // branch has no Error case of its own. The source is IssueDate, because
                    // validation admits no other.
                    Ok (issueDate |> Option.map (fun date -> date.AddDays(float (PaymentTermDays.value paymentTerm))))
                | _ ->
                    match runRule compiledPatterns DueDate rule.Rule content with
                    | Found raw -> extractDate DueDate rule.Hint raw |> Result.map Some
                    | NotFound -> Ok None
                    | TimedOut -> Error (RuleTimedOut(templateId, DueDate))

        return
            {
                SupplierId = ValidTemplate.supplierId template
                TemplateId = templateId
                SourceMessageId = NormalizedMessage.sourceMessageId message
                Reference = reference
                Amount = amount
                Currency = currency
                IssueDate = issueDate
                DueDate = dueDate
            }
    }
