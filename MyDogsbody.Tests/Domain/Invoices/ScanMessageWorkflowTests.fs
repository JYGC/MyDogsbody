module MyDogsbody.Tests.Domain.Invoices.ScanMessageWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Invoices.ScanMessageWorkflow

/// A reader that returns one line per input, naming the source, unless the name is in `broken`.
let private reader (broken: Set<string>) : ReadDocumentText =
    fun source ->
        if Set.contains source.Name broken then
            Error(DocumentUnreadable $"cannot read {source.Name}")
        else
            Ok [ { Text = $"text of {source.Name}"; BlockIndex = 0 } ]

let private attachment name (bytes: byte[]) : MailAttachment =
    { FileName = name; DeclaredContentType = "application/octet-stream"; Content = bytes }

let private message: MailMessage =
    { SourceMessageId = "msg-1"
      Sender = "billing@acme.test"
      Subject = "Your invoice INV-1"
      ReceivedAt = DateTime(2026, 2, 1, 8, 0, 0)
      BodyText = Some "plain body"
      BodyHtml = Some "<p>html body</p>"
      Attachments = [ attachment "invoice.pdf" [| 1uy |] ] }

let private partNames (scanned: ScannedMessage) =
    scanned.Parts |> List.map fst

[<Fact; Trait("Level", "Unit")>]
let ``scanMessage flattens the subject, body and each attachment to parts`` () =
    let scanned, problems = scanMessage (reader Set.empty) message

    Assert.Empty problems
    Assert.Equal(SourceMessageId.value (SourceMessageId.createOrDefault "x" "msg-1"), SourceMessageId.value scanned.SourceMessageId)
    Assert.Equal("billing@acme.test", scanned.Sender)
    Assert.Equal("Your invoice INV-1", scanned.Subject)
    Assert.Equal(DateTime(2026, 2, 1, 8, 0, 0), scanned.ReceivedAt)

    // subject part carries the subject text; body prefers HTML; the pdf attachment is a part
    Assert.Equal<MessagePart list>(
        [ SubjectPart; BodyPart; AttachmentPart("invoice.pdf", Pdf) ],
        partNames scanned
    )

    let subjectLines = scanned.Parts |> List.find (fun (p, _) -> p = SubjectPart) |> snd
    Assert.Equal<string list>([ "Your invoice INV-1" ], subjectLines |> List.map (fun l -> l.Text))

    let bodyLines = scanned.Parts |> List.find (fun (p, _) -> p = BodyPart) |> snd
    Assert.Equal("text of body.html", (List.head bodyLines).Text) // HTML preferred (Finding 5)
    Assert.All(bodyLines, fun line -> Assert.True(line.BlockIndex >= 0))

[<Fact; Trait("Level", "Unit")>]
let ``scanMessage uses the plain-text body when there is no HTML alternative`` () =
    let scanned, _ = scanMessage (reader Set.empty) { message with BodyHtml = None }
    let bodyLines = scanned.Parts |> List.find (fun (p, _) -> p = BodyPart) |> snd
    Assert.Equal("text of body.txt", (List.head bodyLines).Text)

[<Fact; Trait("Level", "Unit")>]
let ``one unreadable attachment is a problem cause and the other parts still arrive`` () =
    let twoAttachments =
        { message with
            Attachments = [ attachment "good.pdf" [| 1uy |]; attachment "bad.pdf" [| 2uy |] ] }

    let scanned, problems = scanMessage (reader (Set.singleton "bad.pdf")) twoAttachments

    // the good attachment and the body are still parts
    Assert.Contains(AttachmentPart("good.pdf", Pdf), partNames scanned)
    Assert.DoesNotContain(AttachmentPart("bad.pdf", Pdf), partNames scanned)

    match problems with
    | [ AttachmentUnreadable(fileName, reason) ] ->
        Assert.Equal("bad.pdf", fileName)
        Assert.Equal("cannot read bad.pdf", reason)
    | other -> Assert.Fail($"Expected one AttachmentUnreadable, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``an attachment whose format has no reader yields FormatUnsupported naming the format`` () =
    let withXlsx = { message with Attachments = [ attachment "statement.xlsx" [| 1uy |] ] }

    let scanned, problems = scanMessage (reader Set.empty) withXlsx

    Assert.DoesNotContain(AttachmentPart("statement.xlsx", Pdf), partNames scanned)

    match problems with
    | [ FormatUnsupported(fileName, format) ] ->
        Assert.Equal("statement.xlsx", fileName)
        Assert.Equal("xlsx", format)
    | other -> Assert.Fail($"Expected one FormatUnsupported, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a legacy .doc attachment yields FormatUnsupported naming "doc"`` () =
    // The Word reader rejects it; ScanMessageWorkflow surfaces that as the problem cause.
    let brokenReader: ReadDocumentText =
        fun source ->
            if source.Name.EndsWith ".doc" then Error(DocumentFormatUnsupported "doc") else Ok []

    let withDoc = { message with Attachments = [ attachment "invoice.doc" [| 1uy |] ] }
    let _, problems = scanMessage brokenReader withDoc

    match problems with
    | [ FormatUnsupported("invoice.doc", "doc") ] -> ()
    | other -> Assert.Fail($"Expected FormatUnsupported(invoice.doc, doc), got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a message with no body has only subject and attachment parts`` () =
    let scanned, _ = scanMessage (reader Set.empty) { message with BodyText = None; BodyHtml = None }
    Assert.DoesNotContain(BodyPart, partNames scanned)
