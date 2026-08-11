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
let private collapseRuns (text: string) : string =
    let result = StringBuilder(text.Length)
    let mutable previousWasSpace = false

    for c in text do
        let isSpaceOrTab = c = ' ' || c = '\t'

        if isSpaceOrTab then
            if not previousWasSpace then
                result.Append(' ') |> ignore

            previousWasSpace <- true
        else
            result.Append(c) |> ignore
            previousWasSpace <- false

    result.ToString()

let private sentenceTerminators = set [ '.'; '!'; '?'; ':' ]

/// Whether current looks like a wrapped continuation of previous: current starts lower-case and
/// previous does not end in a sentence terminator. Private, but its behaviour is asserted
/// through normalize.
let private isContinuation (previous: string) (current: string) : bool =
    not (String.IsNullOrEmpty current)
    && Char.IsLower current.[0]
    && not (String.IsNullOrEmpty previous)
    && not (sentenceTerminators.Contains previous.[previous.Length - 1])

let private normalizeText (text: string) : string =
    text.Normalize(NormalizationForm.FormKC)
    |> foldSpecialSpaces
    |> collapseRuns
    |> fun s -> s.Trim()

/// Finding 4's contract, in one place, applied identically at authoring time and at scan time.
/// Public so the test panel can display exactly what the rules will see - Q7.6.6.
///
/// Order matters and is asserted (TextNormalizationTests): NFKC first - it turns some ligatures
/// and fixed-width forms into their plain equivalents, and it is what turns most non-breaking
/// space variants into a plain space before foldSpecialSpaces or collapseRuns ever see them -
/// then space folding, then collapse, then trim, then the within-block join, then drop empties.
/// Running collapse before NFKC would see untouched non-breaking spaces rather than a collapsible
/// run of plain ones, and leave them all in the output.
let normalize (lines: TextLine list) : TextLine list =
    lines
    |> List.map (fun line -> { line with Text = normalizeText line.Text })
    |> List.fold
        (fun acc line ->
            match acc with
            | previous :: rest when previous.BlockIndex = line.BlockIndex && isContinuation previous.Text line.Text ->
                { previous with Text = previous.Text + " " + line.Text } :: rest
            | _ -> line :: acc)
        []
    |> List.rev
    |> List.filter (fun line -> not (String.IsNullOrEmpty line.Text))
