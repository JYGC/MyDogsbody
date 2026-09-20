module MyDogsbody.Domain.InvoiceTemplates.TextNormalization

open System
open System.Text
open MyDogsbody.Domain.Documents

/// So a rule never has to special-case them. .NET's NormalizationForm.FormKC already decomposes
/// most of these to U+0020 on its own (verified: NBSP, FIGURE SPACE, NARROW NO-BREAK SPACE and
/// IDEOGRAPHIC SPACE all do) - this explicit step exists so the requirement does not rest
/// entirely on that undocumented-to-the-reader side effect, and so the set folded is visible and
/// testable in one place.
let private unicodeSpaceSeparatorsBeyondU0020ThatAreFoldedToAPlainSpace =
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
    String(
        text.ToCharArray()
        |> Array.map (fun character ->
            if unicodeSpaceSeparatorsBeyondU0020ThatAreFoldedToAPlainSpace.Contains character then ' ' else character)
    )

/// It must run after NFKC, which is what turns a non-breaking space into a plain space (and
/// therefore into something collapseRunsOfPlainSpacesAndTabsToOneSpace can see) in the first
/// place.
///
/// A fold carrying "was the previous character a space?" alongside the accumulated characters,
/// rather than a StringBuilder and a mutable flag: the domain centre takes no `mutable` and no
/// statement loops. Accumulating characters in reverse and reversing once keeps it O(n) - the
/// reason not to fold with string concatenation, which would be O(n²) over every line of every
/// scanned document - and is the same shape normalize itself uses below.
let private collapseRunsOfPlainSpacesAndTabsToOneSpace (text: string) : string =
    let collapsed, _ =
        (([], false), text)
        ||> Seq.fold (fun (accumulated, previousWasSpace) character ->
            if character = ' ' || character = '\t' then
                (if previousWasSpace then accumulated else ' ' :: accumulated), true
            else
                character :: accumulated, false)

    String(collapsed |> List.rev |> List.toArray)

let private sentenceTerminators = set [ '.'; '!'; '?'; ':' ]

/// Private, but its behaviour is asserted through normalize.
let private currentLooksLikeAWrappedContinuationOfPrevious (previous: string) (current: string) : bool =
    not (String.IsNullOrEmpty current)
    && Char.IsLower current.[0]
    && not (String.IsNullOrEmpty previous)
    && not (sentenceTerminators.Contains previous.[previous.Length - 1])

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - TextNormalization.fs: normalizeText
let private normalizeText (text: string) : string =
    let safe = if isNull text then "" else text

    let composed =
        try
            safe.Normalize(NormalizationForm.FormKC)
        with :? ArgumentException ->
            safe

    composed
    |> foldSpecialSpaces
    |> collapseRunsOfPlainSpacesAndTabsToOneSpace
    |> fun trimmedText -> trimmedText.Trim()

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - TextNormalization.fs: NormalizedLine
type NormalizedLine = { Line: TextLine; Segments: TextLine list }

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - TextNormalization.fs: normalizeGrouped
let normalizeGrouped (lines: TextLine list) : NormalizedLine list =
    lines
    |> List.map (fun line -> { Line = { line with Text = normalizeText line.Text }; Segments = [] })
    |> List.fold
        (fun accumulatedGroupedLines (grouped: NormalizedLine) ->
            let line = grouped.Line

            match accumulatedGroupedLines with
            | previous :: rest when
                previous.Line.BlockIndex = line.BlockIndex
                && currentLooksLikeAWrappedContinuationOfPrevious previous.Line.Text line.Text
                ->
                { Line = { previous.Line with Text = previous.Line.Text + " " + line.Text }
                  Segments = previous.Segments @ [ line ] }
                :: rest
            | _ -> { grouped with Segments = [ line ] } :: accumulatedGroupedLines)
        []
    |> List.rev
    |> List.filter (fun grouped -> not (String.IsNullOrEmpty grouped.Line.Text))

/// Public so the test panel can display exactly what the rules will see - Q7.6.6.
let normalize (lines: TextLine list) : TextLine list =
    normalizeGrouped lines |> List.map (fun grouped -> grouped.Line)
