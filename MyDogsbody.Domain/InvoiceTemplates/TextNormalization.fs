module MyDogsbody.Domain.InvoiceTemplates.TextNormalization

open System
open System.Text
open MyDogsbody.Domain.Documents

/// The Unicode "space separator" variants beyond U+0020 itself, folded to a plain space so a
/// rule never has to special-case them. .NET's NormalizationForm.FormKC already decomposes most
/// of these to U+0020 on its own (verified: NBSP, FIGURE SPACE, NARROW NO-BREAK SPACE and
/// IDEOGRAPHIC SPACE all do) - this explicit step exists so the requirement does not rest
/// entirely on that undocumented-to-the-reader side effect, and so the set folded is visible and
/// testable in one place.
let private specialSpaces =
    set [
        ' ' // NO-BREAK SPACE
        ' '; ' '; ' '; ' '; ' '; ' '; ' ' // EN QUAD .. SIX-PER-EM SPACE
        ' ' // FIGURE SPACE
        ' '; ' '; ' ' // PUNCTUATION SPACE .. HAIR SPACE
        ' ' // NARROW NO-BREAK SPACE
        ' ' // MEDIUM MATHEMATICAL SPACE
        '　' // IDEOGRAPHIC SPACE
    ]

let private foldSpecialSpaces (text: string) : string =
    String(text.ToCharArray() |> Array.map (fun c -> if specialSpaces.Contains c then ' ' else c))

/// Runs of plain spaces and tabs collapsed to one space. Only recognises ' ' and '\t' - it must
/// run after NFKC, which is what turns a non-breaking space into a plain space (and therefore
/// into something collapseRuns can see) in the first place.
///
/// A fold carrying "was the previous character a space?" alongside the accumulated characters,
/// rather than a StringBuilder and a mutable flag: the domain centre takes no `mutable` and no
/// statement loops. Accumulating characters in reverse and reversing once keeps it O(n) - the
/// reason not to fold with string concatenation, which would be O(n²) over every line of every
/// scanned document - and is the same shape normalize itself uses below.
let private collapseRuns (text: string) : string =
    let collapsed, _ =
        (([], false), text)
        ||> Seq.fold (fun (accumulated, previousWasSpace) c ->
            if c = ' ' || c = '\t' then
                (if previousWasSpace then accumulated else ' ' :: accumulated), true
            else
                c :: accumulated, false)

    String(collapsed |> List.rev |> List.toArray)

let private sentenceTerminators = set [ '.'; '!'; '?'; ':' ]

/// Whether current looks like a wrapped continuation of previous: current starts lower-case and
/// previous does not end in a sentence terminator. Private, but its behaviour is asserted
/// through normalize.
let private isContinuation (previous: string) (current: string) : bool =
    not (String.IsNullOrEmpty current)
    && Char.IsLower current.[0]
    && not (String.IsNullOrEmpty previous)
    && not (sentenceTerminators.Contains previous.[previous.Length - 1])

/// NFKC is the one step here that is not total. String.Normalize raises ArgumentException for any
/// string carrying an unpaired surrogate, and the input is text extracted from PDFs and email
/// bodies - the least trustworthy source in the system, where a truncated or mis-decoded
/// extraction is exactly how a lone surrogate arrives. IsNormalized raises on the same input, so
/// checking first would not avoid the try.
///
/// normalize promises TextLine list -> TextLine list with no failure channel, and an exception
/// escaping here would unwind out of the domain, past a composition root that maps values rather
/// than catching, into a UI with no alert for it - which CLAUDE.md rules out in either ring. So a
/// line that cannot be normalized degrades to its un-normalized self: one malformed glyph costs
/// that line its NFKC folding rather than taking down the whole scan. The remaining steps are
/// per-character and total, so they still apply to it.
let private normalizeText (text: string) : string =
    let safe = if isNull text then "" else text

    let composed =
        try
            safe.Normalize(NormalizationForm.FormKC)
        with :? ArgumentException ->
            safe

    composed |> foldSpecialSpaces |> collapseRuns |> fun s -> s.Trim()

/// One normalized line, together with the lines the document actually laid out to produce it.
///
/// Segments is what makes a line-oriented rule possible at all on top of the continuation join.
/// The join is right for reading text - a hard-wrapped label has to be findable - but it is wrong
/// for COUNTING lines, and LinesAfterLabel counts them. A value on its own line is
/// indistinguishable from a wrapped continuation (both start lower-case under a predecessor that
/// ends in no sentence terminator), so joining silently shifted every offset below a bare label
/// whose value happened to start lower-case: "Reference" / "wu-88213" merged, and
/// LinesAfterLabel("Reference", 1) then returned the line AFTER the value. Whether a template
/// worked depended on the case of the first character of a value its author does not control.
///
/// A line that absorbed no continuation carries exactly itself, so Segments is never empty.
type NormalizedLine = { Line: TextLine; Segments: TextLine list }

/// Finding 4's contract, in one place, applied identically at authoring time and at scan time,
/// with the provenance every consumer needs kept alongside the result.
///
/// Order matters and is asserted (TextNormalizationTests): NFKC first - it turns some ligatures
/// and fixed-width forms into their plain equivalents, and it is what turns most non-breaking
/// space variants into a plain space before foldSpecialSpaces or collapseRuns ever see them -
/// then space folding, then collapse, then trim, then the within-block join, then drop empties.
/// Running collapse before NFKC would see untouched non-breaking spaces rather than a collapsible
/// run of plain ones, and leave them all in the output.
///
/// Empty lines are dropped LAST, after the join, which is what stops a blank line between two
/// lines being read as a wrapped continuation of the first. A dropped line takes its own segment
/// with it: an empty line neither joins nor is joined to (isContinuation is false in both
/// directions), so it is always a group of one.
let normalizeGrouped (lines: TextLine list) : NormalizedLine list =
    lines
    |> List.map (fun line -> { Line = { line with Text = normalizeText line.Text }; Segments = [] })
    |> List.fold
        (fun acc (grouped: NormalizedLine) ->
            let line = grouped.Line

            match acc with
            | previous :: rest when
                previous.Line.BlockIndex = line.BlockIndex && isContinuation previous.Line.Text line.Text
                ->
                { Line = { previous.Line with Text = previous.Line.Text + " " + line.Text }
                  Segments = previous.Segments @ [ line ] }
                :: rest
            | _ -> { grouped with Segments = [ line ] } :: acc)
        []
    |> List.rev
    |> List.filter (fun grouped -> not (String.IsNullOrEmpty grouped.Line.Text))

/// The normalized text itself. Public so the test panel can display exactly what the rules will
/// see - Q7.6.6.
let normalize (lines: TextLine list) : TextLine list =
    normalizeGrouped lines |> List.map (fun grouped -> grouped.Line)
