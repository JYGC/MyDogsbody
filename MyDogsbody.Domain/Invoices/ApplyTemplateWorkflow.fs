module MyDogsbody.Domain.Invoices.ApplyTemplateWorkflow

open System
open System.Globalization
open System.Text.RegularExpressions
open MyDogsbody.Domain
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ApplyTemplateWorkflow.fs: CandidateDocumentContentPlusTheSubject
type private CandidateDocumentContentPlusTheSubject =
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

let private isAttachment (part: NormalizedPart) : bool =
    match part.Part with
    | AttachmentPart _ -> true
    | BodyPart
    | SubjectPart -> false

/// Normalization happened once for the whole message before any template was tried, which is what
/// stops a supplier with N templates paying for NFKC over every line of every attachment N times.
let private contentOfThePartsByGroupingOnlyWithoutNormalizing
    (subject: string)
    (parts: NormalizedPart list)
    : CandidateDocumentContentPlusTheSubject =
    {
        LinesByPart = parts |> List.map (fun selected -> selected.Lines |> List.map (fun grouped -> grouped.Line))
        GroupedByPart = parts |> List.map (fun selected -> selected.Lines)
        AttachmentNames =
            parts
            |> List.choose (fun selected ->
                match selected.Part with
                | AttachmentPart(name, _) -> Some name
                | BodyPart | SubjectPart -> None)
        Subject = subject
    }

type private CandidateDocumentsOfWhichThereIsAlwaysAtLeastOne =
    { FirstCandidate: CandidateDocumentContentPlusTheSubject
      LaterCandidates: CandidateDocumentContentPlusTheSubject list }

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ApplyTemplateWorkflow.fs: selectEveryCandidateDocumentInMessageOrderNeverFewerThanOne
let private selectEveryCandidateDocumentInMessageOrderNeverFewerThanOne
    (part: DocumentPart)
    (message: NormalizedMessage)
    : CandidateDocumentsOfWhichThereIsAlwaysAtLeastOne =
    let subject = NormalizedMessage.subject message

    let selected =
        NormalizedMessage.parts message
        |> List.filter (fun candidate -> partMatchesSelector part candidate.Part)
        |> List.indexed

    // Filtering the indexed list rather than appending the chosen attachment to the singular
    // parts, so every candidate keeps the parts in the order the MESSAGE had them - the order
    // tryFindFirstLineCarryingLabelPartByPartWithItsPart's "the first part carrying the label"
    // depends on.
    let candidateFor (attachmentIndex: int option) =
        selected
        |> List.filter (fun (index, candidate) -> not (isAttachment candidate) || Some index = attachmentIndex)
        |> List.map snd
        |> contentOfThePartsByGroupingOnlyWithoutNormalizing subject

    match selected |> List.filter (snd >> isAttachment) |> List.map fst with
    | [] ->
        { FirstCandidate = candidateFor None
          LaterCandidates = [] }
    | firstAttachment :: laterAttachments ->
        { FirstCandidate = candidateFor (Some firstAttachment)
          LaterCandidates = laterAttachments |> List.map (Some >> candidateFor) }

/// None of them by raising. RegexMatchTimeoutException is caught right here so nothing above
/// this line ever needs to know a Regex is involved.
type private RuleOutcome =
    | Found of string
    | NotFound
    | TimedOut

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ApplyTemplateWorkflow.fs: foundUnlessEmpty
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

/// A TimedOut is treated as a stop rather than retried against later candidates: timing out once
/// on a pathological pattern is already the signal that pattern is dangerous, not a reason to
/// spend the timeout budget again on the next line or filename.
///
/// tryPick, not map-then-tryFind: List.map is eager, so the short-circuit this comment describes
/// did not happen. With the 250ms match timeout a pathological pattern cost 250ms x lines - a
/// 200-line PDF blocked for ~50 seconds, and selectTemplate then repeated that per candidate
/// template. requirements.md: "WHEN a rule times out THE SYSTEM SHALL NOT block the user
/// interface."
let private runRegexOnEachCandidateUntilOneIsFoundOrTimesOut
    (regex: Regex)
    (candidates: string list)
    : RuleOutcome =
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

/// The same line a flattened search would have found. Returned with its part so an offset can be
/// applied inside that part rather than across the whole message.
let private tryFindFirstLineCarryingLabelPartByPartWithItsPart
    (label: string)
    (linesByPart: TextLine list list)
    : (TextLine list * int) option =
    linesByPart
    |> List.tryPick (fun partLines ->
        partLines
        |> List.tryFindIndex (lineCarriesLabel label)
        |> Option.map (fun index -> partLines, index))

/// Segments are joined with exactly one space, so segment k occupies [start, start + length - 1]
/// and the next one starts a single character later. A position landing on a joining space
/// belongs to the segment that follows it, which is the answer a label ending in a space wants.
let rec private indexOfTheLaidOutSegmentContainingPosition
    (position: int)
    (index: int)
    (segments: TextLine list)
    : int =
    match segments with
    | []
    | [ _ ] -> index
    | current :: rest ->
        if position < current.Text.Length then
            index
        else
            indexOfTheLaidOutSegmentContainingPosition (position - current.Text.Length - 1) (index + 1) rest

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ApplyTemplateWorkflow.fs: LaidOutLineAndTheTextARuleLandingOnItReturns
type private LaidOutLineAndTheTextARuleLandingOnItReturns = { Line: TextLine; Text: string }

let private laidOutLinesAndTheTextARuleLandingOnEachReturns
    (groups: TextNormalization.NormalizedLine list)
    : LaidOutLineAndTheTextARuleLandingOnItReturns list =
    groups
    |> List.collect (fun grouped ->
        grouped.Segments
        |> List.mapi (fun index segment ->
            { Line = segment; Text = (if index = 0 then grouped.Line.Text else segment.Text) }))

/// Both halves are deliberate. The SEARCH runs over the joined text, so requirements.md's "WHEN a
/// label hard-wrapped across two lines is matched THE SYSTEM SHALL find the value" still holds -
/// "Amount" / "due:" is one joined line and the label "Amount due" is on it. The OFFSET is then
/// counted over the laid-out lines, because the join is not a line the document has: a value on
/// its own line looks exactly like a wrapped continuation. The label's END, not its start, is what
/// the offset counts from - on a hard-wrapped label the value follows the line the label finishes
/// on.
let private tryLocateLabelInLaidOutLinesAsLinesAndTheIndexOfTheOneItEndsOn
    (label: string)
    (groupsByPart: TextNormalization.NormalizedLine list list)
    : (LaidOutLineAndTheTextARuleLandingOnItReturns list * int) option =
    groupsByPart
    |> List.tryPick (fun groups ->
        groups
        |> List.tryFindIndex (fun grouped -> lineCarriesLabel label grouped.Line)
        |> Option.map (fun groupIndex ->
            let grouped = List.item groupIndex groups
            let labelStart = grouped.Line.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase)
            let precedingLines = groups |> List.truncate groupIndex |> List.sumBy (fun group -> List.length group.Segments)

            let indexOfTheSegmentTheLabelEndsIn =
                indexOfTheLaidOutSegmentContainingPosition (labelStart + label.Length - 1) 0 grouped.Segments

            laidOutLinesAndTheTextARuleLandingOnEachReturns groups, precedingLines + indexOfTheSegmentTheLabelEndsIn))

let private runRule
    (compiledPatterns: Map<TargetField, Regex>)
    (field: TargetField)
    (rule: FieldRule)
    (content: CandidateDocumentContentPlusTheSubject)
    : RuleOutcome =
    match rule with
    | AfterLabel label ->
        match tryFindFirstLineCarryingLabelPartByPartWithItsPart label content.LinesByPart with
        | Some (partLines, index) ->
            let matchedLine = List.item index partLines
            let labelIndex = matchedLine.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase)
            // No Trim here: foundUnlessEmpty trims every outcome, which is what closed the two
            // paths this call site's own Trim never covered.
            foundUnlessEmpty (matchedLine.Text.Substring(labelIndex + label.Length))
        | None -> NotFound
    | LinesAfterLabel(label, offset) ->
        match tryLocateLabelInLaidOutLinesAsLinesAndTheIndexOfTheOneItEndsOn label content.GroupedByPart with
        | Some (laidOutLines, labelIndex) ->
            let targetIndex = labelIndex + offset

            if targetIndex >= 0 && targetIndex < laidOutLines.Length then
                let target = List.item targetIndex laidOutLines

                // requirements.md: "WHEN LinesAfterLabel is given an offset that runs past the
                // end of the BLOCK THE SYSTEM SHALL report that the rule found nothing." A label
                // on the last line of a table cell must not read the first line of the next one,
                // which is the whole reason TextLine carries a BlockIndex.
                if target.Line.BlockIndex = (List.item labelIndex laidOutLines).Line.BlockIndex then
                    foundUnlessEmpty target.Text
                else
                    NotFound
            else
                NotFound
        | None -> NotFound
    | RegexCapture _ ->
        let allLines = content.LinesByPart |> List.collect (List.map (fun candidate -> candidate.Text))
        runRegexOnEachCandidateUntilOneIsFoundOrTimesOut (Map.find field compiledPatterns) allLines
    | FixedValue value -> foundUnlessEmpty value
    | SubjectCapture _ -> runRegexOnce (Map.find field compiledPatterns) content.Subject
    | AttachmentName _ ->
        runRegexOnEachCandidateUntilOneIsFoundOrTimesOut (Map.find field compiledPatterns) content.AttachmentNames
    | DateFromField _ -> NotFound // handled separately in applyTemplate - this rule never reads text

let private thousandsSeparatorFor (decimalSeparator: char) : char =
    if decimalSeparator = ',' then '.' else ','

/// The position is the only thing that tells a credit note's "-245.00" apart from the "-1042"
/// inside "INV-1042", and an accounting document's "(245.00)" apart from a number that merely
/// happens to sit near a bracket. Both questions are about what surrounds the digits rather than
/// what the digits are, so the run carries where to look rather than a copy of one neighbour.
type private MaximalRunOfNumberShapedCharacters = { Text: string; Start: int; End: int }

/// Splitting into runs is what makes "Total for INV-1042: $245.00" two candidates rather than one
/// number. The previous implementation kept every digit, every '-' and the separator with a
/// global String.filter and parsed the concatenation, so - measured - that line booked
/// -1042245.00, "245.00 due 14/07/2026" booked 245.0014072026, and "Ref 2 items $10.50" booked
/// 210.50. All three silently, with no AmountUnparseable and nothing to notice them by.
let private everyMaximalRunOfNumberShapedCharactersInTheTextInOrder
    (decimalSeparator: char)
    (raw: string)
    : MaximalRunOfNumberShapedCharacters list =
    let thousandsSeparator = thousandsSeparatorFor decimalSeparator
    let isNumberShaped character = Char.IsDigit character || character = decimalSeparator || character = thousandsSeparator || character = '-'

    let asRun start (chars: char list) =
        { Text = String(chars |> List.rev |> List.toArray)
          Start = start
          End = start + List.length chars - 1 }

    let completed, trailing, start, _ =
        (([], [], 0, 0), raw)
        ||> Seq.fold (fun (completed, current, start, position) character ->
            if isNumberShaped character then
                let startOfTheRunRememberedOnTheWayPastItsFirstCharacter =
                    if List.isEmpty current then position else start

                completed, character :: current, startOfTheRunRememberedOnTheWayPastItsFirstCharacter, position + 1
            elif List.isEmpty current then
                completed, [], start, position + 1
            else
                asRun start current :: completed, [], start, position + 1)

    (if List.isEmpty trailing then completed else asRun start trailing :: completed) |> List.rev

/// Without this, a hyphenated token that is the only number-shaped run in the text passed the
/// "exactly one candidate" guard and was booked as a negative amount: measured, "INV-1042" gave
/// -1042, "Net-30" gave -30 and "PO-77" gave -77. An Amount rule whose label also appears on a
/// reference line - AfterLabel "Total" against "Total items INV-1042" - is all it takes, and the
/// wrong amount arrives with nothing to notice it by. The shape alone cannot tell that from a
/// genuine credit note; the character in front of it can.
let private runsLeadingHyphenIfAnyIsASignBecauseNoLetterOrDigitPrecedesIt
    (raw: string)
    (run: MaximalRunOfNumberShapedCharacters)
    : bool =
    not (run.Text.StartsWith '-') || run.Start = 0 || not (Char.IsLetterOrDigit raw.[run.Start - 1])

/// Whitespace and currency symbols are stepped over - Char.IsSymbol covers '$', '£', '€' and
/// '¥' - so "($245.00)" reads the same as "(245.00)". A letter, a digit or any other punctuation
/// stops the walk, which is what keeps "Total (net) 245.00" from looking wrapped.
let rec private firstCharacterThatIsNotDecorationWalkingInTheGivenDirection
    (step: int)
    (position: int)
    (raw: string)
    : char option =
    if position < 0 || position >= raw.Length then
        None
    elif Char.IsWhiteSpace raw.[position] || Char.IsSymbol raw.[position] then
        firstCharacterThatIsNotDecorationWalkingInTheGivenDirection step (position + step) raw
    else
        Some raw.[position]

/// "(245.00)" is the other common way a document writes a credit, alongside the trailing CR/DR
/// that requirements.md already has the engine ignore - and measured before this, it booked
/// +245.00. That is the same silent wrong-sign failure the previous round of this function was
/// about, arriving from the other direction: a credit note filed as a charge, with nothing to
/// notice it by.
///
/// An unbalanced bracket is decoration whose meaning cannot be read off the line, and guessing at
/// it is what this function's whole history says not to do.
let private isWrappedInAccountingParenthesesWithBothBracketsPresent
    (raw: string)
    (run: MaximalRunOfNumberShapedCharacters)
    : bool =
    firstCharacterThatIsNotDecorationWalkingInTheGivenDirection -1 (run.Start - 1) raw = Some '('
    && firstCharacterThatIsNotDecorationWalkingInTheGivenDirection 1 (run.End + 1) raw = Some ')'

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ApplyTemplateWorkflow.fs: parseTheOneNumberOutOfTheTextOrNone
let private parseTheOneNumberOutOfTheTextOrNone (decimalSeparator: char) (raw: string) : decimal option =
    let thousandsSeparator = thousandsSeparatorFor decimalSeparator

    let candidates =
        everyMaximalRunOfNumberShapedCharactersInTheTextInOrder decimalSeparator raw
        // A run can END on a separator that was really punctuation - "$245.00." - but never
        // STARTS on one that was, since ".50" is a legitimate way to write half a unit. End moves
        // with the trim, so the closing-bracket check below still looks at the character after
        // the NUMBER rather than after the punctuation.
        |> List.map (fun run ->
            let trimmed = run.Text.TrimEnd(decimalSeparator, thousandsSeparator)
            { run with Text = trimmed; End = run.End - (run.Text.Length - trimmed.Length) })
        |> List.filter (fun run -> run.Text |> Seq.exists Char.IsDigit)

    match candidates with
    | [ single ] when runsLeadingHyphenIfAnyIsASignBecauseNoLetterOrDigitPrecedesIt raw single ->
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
        // Parentheses say credit, so the digits say the magnitude and the brackets say the sign -
        // which makes "(-245.00)" -245.00 rather than a double negative talking itself positive.
        | true, value ->
            if isWrappedInAccountingParenthesesWithBothBracketsPresent raw single then
                Some (-(abs value))
            else
                Some value
        | false, _ -> None
    | _ -> None

/// This is what makes 02/08/2016 read with d/M/yyyy 2 August and the same text read with M/d/yyyy
/// 8 February, deterministically, regardless of the machine's locale.
let private parseDateWithTheExplicitFormatInInvariantCultureNeverAmbientCulture
    (format: string)
    (raw: string)
    : DateTime option =
    match DateTime.TryParseExact(raw.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, value -> Some value
    | false, _ -> None

/// The non-AsMoney branch is unreachable:
/// ValidateTemplateWorkflow refuses to produce a ValidTemplate whose Amount rule is not
/// AsMoney-hinted, which is the save-time refusal that replaced this defaulting to '.' and
/// parsing money out of a rule the user had hinted as text.
let private extractMoney (field: TargetField) (hint: ParseHint) (raw: string) : Result<decimal, InvoiceError> =
    let decimalSeparator =
        match hint with
        | AsMoney separator -> separator
        | AsText
        | AsDate _ -> '.'

    match parseTheOneNumberOutOfTheTextOrNone decimalSeparator raw with
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

    match parseDateWithTheExplicitFormatInInvariantCultureNeverAmbientCulture format raw with
    | Some value -> Ok value
    | None -> Error (DateUnparseable(field, raw, format))

/// Fields are evaluated in a fixed order - Reference, Amount, Currency, IssueDate, DueDate - so
/// that DueDate's DateFromField can read an already-computed IssueDate. This is not a general
/// dependency solver, and it no longer pretends to be one by returning None for the pairings it
/// cannot handle: ValidateTemplateWorkflow refuses at save time every derivation except DueDate
/// from IssueDate, so a forward reference or a longer chain cannot reach this function.
let private applyTemplateToOneCandidateDocument
    (paymentTerm: PaymentTermDays)
    (templateId: TemplateId)
    (template: ValidTemplate)
    (sourceMessageId: SourceMessageId)
    (content: CandidateDocumentContentPlusTheSubject)
    : Result<ExtractedInvoice, InvoiceError> =
    let rules = ValidTemplate.rules template
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
                SourceMessageId = sourceMessageId
                Reference = reference
                Amount = amount
                Currency = currency
                IssueDate = issueDate
                DueDate = dueDate
            }
    }

/// The same choice as
/// SelectTemplateWorkflow.tryEachTemplateInOrderUntilOneSucceedsReportingTheLastErrorWhenAllFail
/// one level up, and for the same reason: a real diagnostic beats "nothing worked".
let rec private tryEachCandidateDocumentUntilOneSucceedsReportingTheLastErrorWhenAllFail
    (applyTo: CandidateDocumentContentPlusTheSubject -> Result<ExtractedInvoice, InvoiceError>)
    (candidate: CandidateDocumentContentPlusTheSubject)
    (remaining: CandidateDocumentContentPlusTheSubject list)
    : Result<ExtractedInvoice, InvoiceError> =
    match applyTo candidate with
    | Ok invoice -> Ok invoice
    | Error lastError ->
        match remaining with
        | [] -> Error lastError
        | next :: rest -> tryEachCandidateDocumentUntilOneSucceedsReportingTheLastErrorWhenAllFail applyTo next rest

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ApplyTemplateWorkflow.fs: applyTemplate
let applyTemplate
    (paymentTerm: PaymentTermDays)
    (templateId: TemplateId)
    (template: ValidTemplate)
    (message: NormalizedMessage)
    : Result<ExtractedInvoice, InvoiceError> =
    let candidateDocuments =
        selectEveryCandidateDocumentInMessageOrderNeverFewerThanOne (ValidTemplate.part template) message

    tryEachCandidateDocumentUntilOneSucceedsReportingTheLastErrorWhenAllFail
        (applyTemplateToOneCandidateDocument
            paymentTerm
            templateId
            template
            (NormalizedMessage.sourceMessageId message))
        candidateDocuments.FirstCandidate
        candidateDocuments.LaterCandidates
