/// Returns a ScannedMessage AND a list of problem causes rather than a Result (design decision
/// 3): one corrupt attachment out of two must not lose the invoice in the other. A part that
/// could not be read simply is not in Parts, and its reason is in the causes list.
module MyDogsbody.Domain.Invoices.ScanMessageWorkflow

open System.Text
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices

type private PartOutcome =
    | Part of MessagePart * TextLine list
    | Problem of ScanProblemCause
    | Nothing

let private bodySourcePreferringTheAlternativeThatPreservesBlockStructure
    (message: MailMessage)
    : DocumentSource option =
    // Finding 5. The trivial "which alternative exists" choice is here; EmailBodyReader does the
    // structural work, PlainTextDocumentReader handles a plain-only body.
    match message.BodyHtml, message.BodyText with
    | Some html, _ when not (System.String.IsNullOrWhiteSpace html) ->
        Some { Format = EmailBody; Name = "body.html"; Content = Encoding.UTF8.GetBytes html }
    | _, Some text when not (System.String.IsNullOrWhiteSpace text) ->
        Some { Format = PlainText; Name = "body.txt"; Content = Encoding.UTF8.GetBytes text }
    | _ -> None

let private readPart (readDocumentText: ReadDocumentText) (part: MessagePart) (source: DocumentSource) : PartOutcome =
    let fileName =
        match part with
        | AttachmentPart(name, _) -> name
        | BodyPart -> "(message body)"
        | SubjectPart -> "(subject)"

    match readDocumentText source with
    | Ok lines -> Part(part, lines)
    | Error DocumentHasNoTextLayer -> Problem(AttachmentUnreadable(fileName, "no extractable text layer"))
    | Error(DocumentUnreadable reason) -> Problem(AttachmentUnreadable(fileName, reason))
    | Error(DocumentFormatUnsupported format) -> Problem(FormatUnsupported(fileName, format))
    | Error(DocumentPathInvalid reason) -> Problem(AttachmentUnreadable(fileName, reason))

let private attachmentOutcome (readDocumentText: ReadDocumentText) (attachment: MailAttachment) : PartOutcome =
    match DocumentFormat.ofFileName attachment.FileName with
    | Ok format ->
        readPart
            readDocumentText
            (AttachmentPart(attachment.FileName, format))
            { Format = format; Name = attachment.FileName; Content = attachment.Content }
    | Error extension -> Problem(FormatUnsupported(attachment.FileName, extension))

let scanMessage
    (readDocumentText: ReadDocumentText)
    (message: MailMessage)
    : ScannedMessage * ScanProblemCause list =

    let subjectText = if isNull message.Subject then "" else message.Subject
    let subjectPartWithItsTextLeftForMessageNormalizationToFold =
        Part(SubjectPart, [ { Text = subjectText; BlockIndex = 0 } ])

    let bodyOutcome =
        match bodySourcePreferringTheAlternativeThatPreservesBlockStructure message with
        | Some source -> readPart readDocumentText BodyPart source
        | None -> Nothing

    let attachmentOutcomes =
        message.Attachments |> List.map (attachmentOutcome readDocumentText)

    let outcomes = subjectPartWithItsTextLeftForMessageNormalizationToFold :: bodyOutcome :: attachmentOutcomes

    let parts =
        outcomes
        |> List.choose (function
            | Part(part, lines) -> Some(part, lines)
            | Problem _
            | Nothing -> None)

    let problems =
        outcomes
        |> List.choose (function
            | Problem cause -> Some cause
            | Part _
            | Nothing -> None)

    let scanned =
        { SourceMessageId =
            SourceMessageId.createOrDefault $"unidentified:{message.ReceivedAt.Ticks}" message.SourceMessageId
          Sender = (if isNull message.Sender then "" else message.Sender)
          Subject = subjectText
          ReceivedAt = message.ReceivedAt
          Parts = parts }

    scanned, problems
