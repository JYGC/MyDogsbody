module MyDogsbody.Tests.Contracts.MessageNormalizationContractTests

open System
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices

// MessageNormalization.normalizeMessage is the SOLE DOOR to NormalizedMessage: the record is
// private to the Invoices namespace and this is the only function that builds the literal.
// ApplyTemplateWorkflow is then written against a guarantee it never re-checks - that anything
// holding this type has had its subject, its attachment filenames and both views of every part's
// lines put through TextNormalization exactly once.
//
// That guarantee is what this suite pins. CLAUDE.md -> Testing -> Contract asks for every mapper
// at a ring boundary to be asserted field-for-field, and normalizeMessage is that mapper for the
// Invoices area's stage boundary: ScannedMessage (untrusted text, straight off a reader) ->
// NormalizedMessage (the only thing the engine accepts). Its own unit tests assert what it does
// with a given input; these assert the properties applyTemplate LEANS on, so a later change that
// reaches NormalizedMessage by another route - a second constructor, a store loading one back,
// a normalization step moved elsewhere - breaks here rather than silently in the engine. The
// un-normalized-subject defect of review round 1 was exactly that drift, found by hand.
//
// DEFERRED, deliberately, and not silently: the error-translation rows (each InvoiceError and
// TemplateError case to its intended MyDogsbodyException) need TemplateApiMappers, which lands in
// PR #12 with the store and the API record. A dependency-function-type suite is likewise thin
// value while the Invoices area still declares no dependency types and has no adapter to run one
// against - change #4 introduces both, and owes the suite then.

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private line blockIndex text : TextLine = { Text = text; BlockIndex = blockIndex }

let private scannedMessage subject (parts: (MessagePart * TextLine list) list) : ScannedMessage =
    {
        SourceMessageId = SourceMessageId.create "msg-1" |> valueOrFail
        Sender = "billing@acme.example"
        Subject = subject
        ReceivedAt = DateTime(2026, 7, 14)
        Parts = parts
    }

/// Text carrying one of everything TextNormalization is responsible for: a non-breaking space, a
/// run of plain spaces, surrounding whitespace and a full-width digit.
let private messy = "  Total:   10０.00 "

let private allText (message: NormalizedMessage) =
    [ yield NormalizedMessage.subject message

      for part in NormalizedMessage.parts message do
          match part.Part with
          | AttachmentPart(name, _) -> yield name
          | BodyPart
          | SubjectPart -> ()

          for grouped in part.Lines do
              yield grouped.Line.Text
              yield! grouped.Segments |> List.map (fun segment -> segment.Text) ]

// ---------- the guarantee: every piece of text a rule can see is normalized ----------

[<Fact; Trait("Level", "Contract")>]
let ``every piece of text a NormalizedMessage carries equals that text put through the normalization directly`` () =
    // Field-for-field in the direction that matters: not "something was normalized" but "each of
    // these five places holds exactly what TextNormalization would have produced for it". A new
    // text-carrying field added to ScannedMessage and forgotten here fails on the field it added.
    let raw =
        scannedMessage
            messy
            [ BodyPart, [ line 0 messy; line 0 "Reference"; line 0 "wu-88213" ]
              AttachmentPart(messy, Pdf), [ line 0 messy ] ]

    let actual = MessageNormalization.normalizeMessage raw

    Assert.Equal(InvoiceText.normalizeLine messy, NormalizedMessage.subject actual)

    match NormalizedMessage.parts actual with
    | [ body; attachment ] ->
        Assert.Equal<MessagePart>(BodyPart, body.Part)
        Assert.Equal<TextNormalization.NormalizedLine list>(
            TextNormalization.normalizeGrouped [ line 0 messy; line 0 "Reference"; line 0 "wu-88213" ],
            body.Lines)

        Assert.Equal<MessagePart>(AttachmentPart(InvoiceText.normalizeLine messy, Pdf), attachment.Part)
        Assert.Equal<TextNormalization.NormalizedLine list>(
            TextNormalization.normalizeGrouped [ line 0 messy ],
            attachment.Lines)
    | other -> Assert.Fail($"Expected a body part and an attachment part, but got {other}")

[<Fact; Trait("Level", "Contract")>]
let ``no text a NormalizedMessage carries still holds anything the normalization removes`` () =
    // The same guarantee stated as the property the engine actually depends on, so it holds for
    // text this suite never enumerated: a pattern authored against the normalized display can
    // only match at scan time if nothing here still carries a non-breaking space, a full-width
    // digit, a doubled space or surrounding whitespace.
    let raw =
        scannedMessage
            messy
            [ BodyPart, [ line 0 messy; line 1 messy ]
              AttachmentPart(messy, Pdf), [ line 0 messy ]
              AttachmentPart("second０.pdf", Word), [] ]

    let actual = MessageNormalization.normalizeMessage raw

    for text in allText actual do
        Assert.DoesNotContain(" ", text)
        Assert.DoesNotContain("０", text)
        Assert.DoesNotContain("  ", text)
        Assert.Equal(text.Trim(), text)

// ---------- the guarantee: it happened exactly once ----------

let private messyMessage () =
    scannedMessage
        messy
        [ BodyPart, [ line 0 messy; line 0 "Reference"; line 0 "wu-88213"; line 0 "   " ]
          AttachmentPart(messy, Pdf), [ line 1 messy ] ]

[<Fact; Trait("Level", "Contract")>]
let ``feeding a message's own laid-out lines back in reproduces it exactly`` () =
    // The fixed point that makes hoisting the normalization above selectTemplate's loop safe: the
    // engine sees the same thing whether normalization runs once per message or once per candidate
    // template. Fed with the LAID-OUT lines - the lines as the document had them - the result is
    // identical down to the provenance, which is what a second, later door to NormalizedMessage
    // would have to preserve.
    let raw = messyMessage ()
    let once = MessageNormalization.normalizeMessage raw

    let twice =
        MessageNormalization.normalizeMessage
            { raw with
                Subject = NormalizedMessage.subject once
                Parts =
                    NormalizedMessage.parts once
                    |> List.map (fun part ->
                        part.Part, part.Lines |> List.collect (fun grouped -> grouped.Segments)) }

    Assert.Equal<NormalizedMessage>(once, twice)

[<Fact; Trait("Level", "Contract")>]
let ``feeding a message's own joined lines back in reproduces the same text, though not the same layout`` () =
    // Stated rather than hidden, because it is the one half of idempotence that does NOT hold and
    // a reader would otherwise assume it did: the joined text is a fixed point, the provenance is
    // not. Segments are the lines a DOCUMENT laid out, and joined text no longer has that layout -
    // "Reference" / "wu-88213" comes back as one segment rather than two. Nothing depends on
    // recovering it: normalizeMessage is only ever handed a freshly scanned message, and the type
    // system gives no other way to reach this function.
    let raw = messyMessage ()
    let once = MessageNormalization.normalizeMessage raw

    let twice =
        MessageNormalization.normalizeMessage
            { raw with
                Subject = NormalizedMessage.subject once
                Parts =
                    NormalizedMessage.parts once
                    |> List.map (fun part -> part.Part, part.Lines |> List.map (fun grouped -> grouped.Line)) }

    Assert.Equal(NormalizedMessage.subject once, NormalizedMessage.subject twice)

    Assert.Equal<(MessagePart * string list) list>(
        NormalizedMessage.parts once
        |> List.map (fun part -> part.Part, part.Lines |> List.map (fun grouped -> grouped.Line.Text)),
        NormalizedMessage.parts twice
        |> List.map (fun part -> part.Part, part.Lines |> List.map (fun grouped -> grouped.Line.Text)))

// ---------- the guarantee: nothing else about the message changed ----------

[<Fact; Trait("Level", "Contract")>]
let ``every part survives normalization in its original order, with its kind and format intact`` () =
    // selectContent filters on MessagePart and LinesAfterLabel indexes within one part, so both
    // the identity and the ORDER of the parts are load-bearing, not incidental.
    let raw =
        scannedMessage
            ""
            [ BodyPart, []
              AttachmentPart("first.pdf", Pdf), []
              AttachmentPart("second.docx", Word), []
              AttachmentPart("third.txt", PlainText), []
              AttachmentPart("fourth.eml", EmailBody), [] ]

    let actual = MessageNormalization.normalizeMessage raw

    Assert.Equal<MessagePart list>(
        [ BodyPart
          AttachmentPart("first.pdf", Pdf)
          AttachmentPart("second.docx", Word)
          AttachmentPart("third.txt", PlainText)
          AttachmentPart("fourth.eml", EmailBody) ],
        NormalizedMessage.parts actual |> List.map (fun part -> part.Part))

[<Fact; Trait("Level", "Contract")>]
let ``the source message id crosses the boundary unmapped, since ExtractedInvoice reads it from here`` () =
    // ExtractedInvoice.SourceMessageId comes off the NormalizedMessage, not off the ScannedMessage
    // the caller still holds, so this is the field a rename or a re-parse would break silently.
    let actual = MessageNormalization.normalizeMessage (scannedMessage "anything" [])

    Assert.Equal("msg-1", SourceMessageId.value (NormalizedMessage.sourceMessageId actual))

[<Fact; Trait("Level", "Contract")>]
let ``a block boundary survives normalization, because LinesAfterLabel refuses to cross one`` () =
    // BlockIndex is carried, not renumbered: the engine's block check compares the label's line to
    // the target's, so a normalization that reset or collapsed the indices would silently turn
    // that refusal into a pass.
    let raw =
        scannedMessage
            ""
            [ BodyPart, [ line 0 "Reference"; line 1 "wu-88213"; line 7 "Total: 100.00" ] ]

    let actual = MessageNormalization.normalizeMessage raw

    match NormalizedMessage.parts actual with
    | [ body ] ->
        Assert.Equal<int list>([ 0; 1; 7 ], body.Lines |> List.map (fun grouped -> grouped.Line.BlockIndex))
        Assert.Equal<int list>(
            [ 0; 1; 7 ],
            body.Lines |> List.collect (fun grouped -> grouped.Segments |> List.map (fun s -> s.BlockIndex)))
    | other -> Assert.Fail($"Expected one body part, but got {other}")

// ---------- the guarantee: the two views of a part's lines agree ----------

[<Fact; Trait("Level", "Contract")>]
let ``a part's segments are exactly its laid-out lines, and its joined lines are built from them`` () =
    // The two views ApplyTemplateWorkflow reads - joined lines for every text rule, laid-out lines
    // for LinesAfterLabel's offset - are produced by one pass, and this is the invariant that
    // makes them safe to read side by side: every segment is non-empty, and a joined line is its
    // segments separated by single spaces.
    let raw =
        scannedMessage
            ""
            [ BodyPart, [ line 0 "Please contact"; line 0 "us for help."; line 0 "Reference"; line 0 "WU-1" ] ]

    let actual = MessageNormalization.normalizeMessage raw

    match NormalizedMessage.parts actual with
    | [ body ] ->
        Assert.All(
            body.Lines,
            fun grouped ->
                Assert.NotEmpty grouped.Segments
                Assert.All(grouped.Segments, fun segment -> Assert.False(String.IsNullOrEmpty segment.Text))
                Assert.Equal(String.Join(" ", grouped.Segments |> List.map (fun s -> s.Text)), grouped.Line.Text))

        Assert.Equal<string list>(
            [ "Please contact us for help."; "Reference"; "WU-1" ],
            body.Lines |> List.map (fun grouped -> grouped.Line.Text))

        Assert.Equal<string list>(
            [ "Please contact"; "us for help."; "Reference"; "WU-1" ],
            body.Lines |> List.collect (fun grouped -> grouped.Segments |> List.map (fun s -> s.Text)))
    | other -> Assert.Fail($"Expected one body part, but got {other}")
