module MyDogsbody.Tests.Domain.Invoices.MessageNormalizationTests

open System
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Invoices

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

// PR #11 review, finding 8: the subject and every attachment filename are text a rule is
// evaluated against, so they go through the same normalization the body lines do.

[<Fact; Trait("Level", "Unit")>]
let ``normalizeMessage normalizes the subject`` () =
    // A non-breaking space, a run of plain spaces and surrounding whitespace - all three of the
    // things TextNormalization is responsible for, in the one place mailers most often put them.
    let actual = MessageNormalization.normalizeMessage (scannedMessage "  Your\u00A0invoice   REF-1 is attached  " [])

    Assert.Equal("Your invoice REF-1 is attached", NormalizedMessage.subject actual)

[<Fact; Trait("Level", "Unit")>]
let ``normalizeMessage normalizes every attachment filename`` () =
    // Full-width digits fold to plain ASCII under NFKC. Without this the AttachmentName pattern
    // still matched them - \d covers the Nd category - and quietly stored a reference made of
    // characters no other source of the same reference would ever produce.
    let actual =
        MessageNormalization.normalizeMessage (
            scannedMessage
                ""
                [ BodyPart, []
                  AttachmentPart("９０３４５２１.pdf", Pdf), []
                  AttachmentPart("  cover  note.pdf  ", Pdf), [] ]
        )

    let names =
        NormalizedMessage.parts actual
        |> List.choose (fun part ->
            match part.Part with
            | AttachmentPart(name, _) -> Some name
            | BodyPart
            | SubjectPart -> None)

    Assert.Equal<string list>([ "9034521.pdf"; "cover note.pdf" ], names)

[<Fact; Trait("Level", "Unit")>]
let ``normalizeMessage normalizes each part's lines and drops the empty ones`` () =
    let actual =
        MessageNormalization.normalizeMessage (
            scannedMessage "" [ BodyPart, [ line 0 "  Total:\u00A0  100.00 "; line 0 "   "; line 0 "AUD" ] ]
        )

    match NormalizedMessage.parts actual with
    | [ { Part = BodyPart; Lines = lines } ] ->
        Assert.Equal<string list>([ "Total: 100.00"; "AUD" ], lines |> List.map (fun l -> l.Line.Text))
    | other -> Assert.Fail($"Expected one body part, but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``normalizeMessage never joins a wrapped continuation across two parts`` () =
    // Each part is normalized on its own. "continued" starts lower-case and "Invoice for" does
    // not end in a sentence terminator, so within ONE part these would be joined - across two
    // they must not be, because they are different documents.
    let actual =
        MessageNormalization.normalizeMessage (
            scannedMessage
                ""
                [ AttachmentPart("first.pdf", Pdf), [ line 0 "Invoice for" ]
                  AttachmentPart("second.pdf", Pdf), [ line 0 "continued overleaf" ] ]
        )

    let allLines =
        NormalizedMessage.parts actual
        |> List.collect (fun part -> part.Lines |> List.map (fun l -> l.Line.Text))

    Assert.Equal<string list>([ "Invoice for"; "continued overleaf" ], allLines)

[<Fact; Trait("Level", "Unit")>]
let ``normalizeMessage carries the source message id through untouched`` () =
    let actual = MessageNormalization.normalizeMessage (scannedMessage "anything" [])

    Assert.Equal("msg-1", SourceMessageId.value (NormalizedMessage.sourceMessageId actual))

// PR #11 review, finding 15: normalization is hoisted out of the per-template loop and happens
// once per message. There is no behavioural difference to assert - the results were always the
// same, only recomputed N times - so what is pinned instead is the property that makes hoisting
// safe, plus the type that makes it unskippable: applyTemplate takes a NormalizedMessage and has
// no ScannedMessage to normalize even if it wanted to.

[<Fact; Trait("Level", "Unit")>]
let ``normalizing an already-normalized message changes nothing`` () =
    let raw =
        scannedMessage
            "  Your\u00A0invoice   REF-1  "
            [ BodyPart, [ line 0 "  Total:\u00A0 100.00 "; line 0 "   " ]
              AttachmentPart("  ９０３.pdf ", Pdf), [ line 0 "Reference"; line 1 "R-1" ] ]

    let once = MessageNormalization.normalizeMessage raw

    // Feed the normalized text back in as if it were a freshly scanned message - the LAID-OUT
    // lines, which is what a scanned message carries. Feeding the joined lines instead would lose
    // the layout they were joined from, which is a property of joining rather than of this
    // function; Contracts/MessageNormalizationContractTests pins both halves.
    let twice =
        MessageNormalization.normalizeMessage
            { raw with
                Subject = NormalizedMessage.subject once
                Parts =
                    NormalizedMessage.parts once
                    |> List.map (fun part -> part.Part, part.Lines |> List.collect (fun l -> l.Segments)) }

    Assert.Equal<NormalizedMessage>(once, twice)
