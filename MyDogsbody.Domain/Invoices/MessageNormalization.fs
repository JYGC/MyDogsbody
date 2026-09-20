/// The one door to NormalizedMessage. Not a workflow - it takes no dependencies, decides
/// nothing, and cannot fail; it is the normalization step of the engine's pipeline, hoisted out
/// of it so it runs once per message rather than once per candidate template.
module MyDogsbody.Domain.Invoices.MessageNormalization

open MyDogsbody.Domain.InvoiceTemplates

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - MessageNormalization.fs: normalizeMessage
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
