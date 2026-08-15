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
///
/// GroupedByPart carries the same text with its provenance - which laid-out lines each joined line
/// was built from - because LinesAfterLabel counts laid-out lines while every other rule reads
/// joined ones. See TextNormalization.NormalizedLine for why the two differ.
type private SelectedContent =
    { LinesByPart: TextLine list list
      GroupedByPart: TextNormalization.NormalizedLine list list
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
        |> List.filter (fun selected -> partMatchesSelector part selected.Part)

    {
        LinesByPart =
            selectedParts
            |> List.map (fun selected -> selected.Lines |> List.map (fun grouped -> grouped.Line))
        GroupedByPart = selectedParts |> List.map (fun selected -> selected.Lines)
        AttachmentNames =
            selectedParts
            |> List.choose (fun selected ->
                match selected.Part with
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
///
/// The TRIM lives here for the same reason the emptiness check does: it belongs to every outcome,
/// not to whichever call site remembers it. AfterLabel used to trim its own substring, so
/// FixedValue and a capture group were the two paths that did not - and Currency is the one field
/// with no later parse step to trim it, so " AUD " reached ExtractedInvoice verbatim and would
/// split change #4's natural key against a sibling template's "AUD".
let private foundUnlessEmpty (text: string) : RuleOutcome =
    if String.IsNullOrWhiteSpace text then NotFound else Found (text.Trim())

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

/// String.IndexOf(null, StringComparison) RAISES ArgumentNullException, and a label reaches here
/// from a stored FieldRule - across the outer-ring boundary, where a NULL column is how a null
/// string arrives. ValidateTemplateWorkflow refuses an empty or null label at save time, so the
/// only door to a ValidTemplate cannot produce one; this guard is the same defence in depth
/// runRegexOnce keeps behind compilePattern's own null check, for the same reason - an exception
/// escaping here would unwind out of the domain, past a composition root that maps values rather
/// than catching, into a UI with no alert for it.
let private lineCarriesLabel (label: string) (line: TextLine) : bool =
    not (isNull label) && line.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0

/// The first line carrying the label, searched part by part in order - the same line a flattened
/// search would have found, returned together with the part it belongs to so an offset can be
/// applied inside that part rather than across the whole message.
let private tryFindLabelledLine (label: string) (linesByPart: TextLine list list) : (TextLine list * int) option =
    linesByPart
    |> List.tryPick (fun partLines ->
        partLines
        |> List.tryFindIndex (lineCarriesLabel label)
        |> Option.map (fun index -> partLines, index))

/// Which of a joined line's laid-out segments a given character position falls in. Segments are
/// joined with exactly one space, so segment k occupies [start, start + length - 1] and the next
/// one starts a single character later. A position landing on a joining space belongs to the
/// segment that follows it, which is the answer a label ending in a space wants.
let rec private segmentIndexOf (position: int) (index: int) (segments: TextLine list) : int =
    match segments with
    | []
    | [ _ ] -> index
    | current :: rest ->
        if position < current.Text.Length then
            index
        else
            segmentIndexOf (position - current.Text.Length - 1) (index + 1) rest

/// Where a label sits in a part's LAID-OUT lines: those lines, and the index of the one the
/// label's match ends on.
///
/// Both halves are deliberate. The SEARCH runs over the joined text, so requirements.md's "WHEN a
/// label hard-wrapped across two lines is matched THE SYSTEM SHALL find the value" still holds -
/// "Amount" / "due:" is one joined line and the label "Amount due" is on it. The OFFSET is then
/// counted over the laid-out lines, because the join is not a line the document has: a value on
/// its own line looks exactly like a wrapped continuation, so counting joined lines made
/// LinesAfterLabel("Reference", 1) return the value on "Reference" / "WU-88213" and the line after
/// it on "Reference" / "wu-88213". The label's END, not its start, is what the offset counts from -
/// on a hard-wrapped label the value follows the line the label finishes on.
let private tryFindLabelInLaidOutLines
    (label: string)
    (groupsByPart: TextNormalization.NormalizedLine list list)
    : (TextLine list * int) option =
    groupsByPart
    |> List.tryPick (fun groups ->
        groups
        |> List.tryFindIndex (fun grouped -> lineCarriesLabel label grouped.Line)
        |> Option.map (fun groupIndex ->
            let grouped = List.item groupIndex groups
            let labelStart = grouped.Line.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase)
            let precedingLines = groups |> List.truncate groupIndex |> List.sumBy (fun g -> List.length g.Segments)
            let laidOutLines = groups |> List.collect (fun g -> g.Segments)

            laidOutLines, precedingLines + segmentIndexOf (labelStart + label.Length - 1) 0 grouped.Segments))

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
            // No Trim here: foundUnlessEmpty trims every outcome, which is what closed the two
            // paths this call site's own Trim never covered.
            foundUnlessEmpty (matchedLine.Text.Substring(labelIndex + label.Length))
        | None -> NotFound
    | LinesAfterLabel(label, offset) ->
        match tryFindLabelInLaidOutLines label content.GroupedByPart with
        | Some (laidOutLines, labelIndex) ->
            let targetIndex = labelIndex + offset

            if targetIndex >= 0 && targetIndex < laidOutLines.Length then
                let target = List.item targetIndex laidOutLines

                // requirements.md: "WHEN LinesAfterLabel is given an offset that runs past the
                // end of the BLOCK THE SYSTEM SHALL report that the rule found nothing." A label
                // on the last line of a table cell must not read the first line of the next one,
                // which is the whole reason TextLine carries a BlockIndex.
                if target.BlockIndex = (List.item labelIndex laidOutLines).BlockIndex then
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

/// A maximal run of number-shaped characters, and the character immediately before it - which is
/// the only thing that tells a credit note's "-245.00" apart from the "-1042" inside "INV-1042".
type private NumericRun = { Text: string; PrecededBy: char option }

/// Every maximal run of number-shaped characters in the text, in order.
///
/// Splitting into runs is what makes "Total for INV-1042: $245.00" two candidates rather than one
/// number. The previous implementation kept every digit, every '-' and the separator with a
/// global String.filter and parsed the concatenation, so - measured - that line booked
/// -1042245.00, "245.00 due 14/07/2026" booked 245.0014072026, and "Ref 2 items $10.50" booked
/// 210.50. All three silently, with no AmountUnparseable and nothing to notice them by.
let private numericRuns (decimalSeparator: char) (raw: string) : NumericRun list =
    let thousandsSeparator = thousandsSeparatorFor decimalSeparator
    let isNumberShaped c = Char.IsDigit c || c = decimalSeparator || c = thousandsSeparator || c = '-'

    let asRun precededBy (chars: char list) =
        { Text = String(chars |> List.rev |> List.toArray); PrecededBy = precededBy }

    let completed, trailing, precededBy, _ =
        (([], [], None, None), raw)
        ||> Seq.fold (fun (completed, current, precededBy, previous) character ->
            if isNumberShaped character then
                // The character before the run STARTED, remembered on the way past it.
                let precededBy = if List.isEmpty current then previous else precededBy
                completed, character :: current, precededBy, Some character
            elif List.isEmpty current then
                completed, [], precededBy, Some character
            else
                asRun precededBy current :: completed, [], None, Some character)

    (if List.isEmpty trailing then completed else asRun precededBy trailing :: completed) |> List.rev

/// Whether a run's leading '-' is a SIGN rather than a joiner inside something that is not a
/// number. A sign appears at the start of the text, after whitespace, after a currency symbol or
/// after punctuation - never immediately after a letter or a digit.
///
/// Without this, a hyphenated token that is the only number-shaped run in the text passed the
/// "exactly one candidate" guard and was booked as a negative amount: measured, "INV-1042" gave
/// -1042, "Net-30" gave -30 and "PO-77" gave -77. An Amount rule whose label also appears on a
/// reference line - AfterLabel "Total" against "Total items INV-1042" - is all it takes, and the
/// wrong amount arrives with nothing to notice it by. The shape alone cannot tell that from a
/// genuine credit note; the character in front of it can.
let private hasSignInSignPosition (run: NumericRun) : bool =
    if run.Text.StartsWith '-' then
        match run.PrecededBy with
        | None -> true
        | Some character -> not (Char.IsLetterOrDigit character)
    else
        true

/// One number out of the text, or nothing. Currency symbols, thousands separators, a trailing
/// CR/DR suffix and a full stop ending the sentence all fall away; a SECOND number anywhere in
/// the text does not. Two candidates is an ambiguity this reports rather than resolves - guessing
/// puts a wrong amount in the ledger with nothing to notice it by, which is the failure
/// requirements.md's "never a default ... silently substituted" is written against.
///
/// A run whose '-' is not in a sign position is not an amount at all, and does not become one by
/// being the only number-shaped thing on the line - so it falls through to the same refusal two
/// candidates get rather than to a second, quieter answer.
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
        |> List.map (fun run -> { run with Text = run.Text.TrimEnd(decimalSeparator, thousandsSeparator) })
        |> List.filter (fun run -> run.Text |> Seq.exists Char.IsDigit)

    match candidates with
    | [ single ] when hasSignInSignPosition single ->
        let withoutGrouping = single.Text.Replace(string thousandsSeparator, "")

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
                    // Arithmetic over an already-computed value - but NOT total: DateTime.AddDays
                    // raises ArgumentOutOfRangeException once the result leaves DateTime's range,
                    // and an issue date read as 31 Dec 9999 with any positive term does exactly
                    // that. This branch used to claim it never failed and had no Error case, so
                    // the exception unwound out of a workflow whose signature promises a Result.
                    // PaymentTermDays is constrained to 0..365, so only the upper end can
                    // overflow. The source is IssueDate, because validation admits no other.
                    match issueDate with
                    | None -> Ok None
                    | Some date ->
                        let days = PaymentTermDays.value paymentTerm

                        if float days > (DateTime.MaxValue - date).TotalDays then
                            Error (DueDateOutOfRange(templateId, date, days))
                        else
                            Ok (Some (date.AddDays(float days)))
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
