/// The one door to NormalizedMessage. Not a workflow - it takes no dependencies, decides
/// nothing, and cannot fail; it is the normalization step of the engine's pipeline, hoisted out
/// of it so it runs once per message rather than once per candidate template.
module MyDogsbody.Domain.Invoices.MessageNormalization

open MyDogsbody.Domain.InvoiceTemplates

/// Puts every piece of text a rule can be evaluated against through TextNormalization: each
/// part's lines, the subject, and each attachment's filename.
///
/// The subject and the filenames used to be handed to the regex verbatim while only the body
/// lines were normalized. Mail subjects are one of the likeliest places to carry U+00A0 / U+202F
/// (mailers insert them around numbers) and NFKC-decomposable full-width forms, so a
/// SubjectCapture pattern authored against the test panel's normalized display would match there
/// and silently match nothing at scan time - requirements.md's "WHEN any rule is evaluated" is
/// explicit that this applies to any rule, not to the document body alone.
///
/// Each part's lines are normalized on their own, so the within-block continuation join never
/// runs across two parts: they are different documents and their BlockIndex numbering is
/// unrelated.
///
/// normalizeGrouped rather than normalize: the grouped form carries which laid-out lines each
/// joined line was built from, which is what LinesAfterLabel counts its offset over. Producing it
/// here, in the one pass that already runs once per message, is what keeps the two views of the
/// same text from ever disagreeing.
let normalizeMessage (message: ScannedMessage) : NormalizedMessage =
    let normalizePart (part: MessagePart) : MessagePart =
        match part with
        | AttachmentPart(name, format) -> AttachmentPart(InvoiceText.normalizeLine name, format)
        | BodyPart
        | SubjectPart -> part

    {
        SourceMessageId' = message.SourceMessageId
        Subject' = InvoiceText.normalizeLine message.Subject
        Parts' =
            message.Parts
            |> List.map (fun (part, lines) ->
                { Part = normalizePart part; Lines = TextNormalization.normalizeGrouped lines })
    }
