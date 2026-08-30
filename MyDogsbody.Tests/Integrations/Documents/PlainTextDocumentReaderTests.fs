module MyDogsbody.Tests.Integrations.Documents.PlainTextDocumentReaderTests

open System.Text
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Documents

let private source (bytes: byte[]) : DocumentSource =
    { Format = PlainText; Name = "attachment.txt"; Content = bytes }

let private utf8 (text: string) = Encoding.UTF8.GetBytes text

[<Fact; Trait("Level", "Unit")>]
let ``readText decodes UTF-8, non-ASCII intact`` () =
    let actual = PlainTextDocumentReader.readText (source (utf8 "Montréal café — £100.00"))

    match actual with
    | Ok [ line ] -> Assert.Equal("Montréal café — £100.00", line.Text)
    | other -> Assert.Fail($"Expected one line, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``readText splits on line breaks and keeps order`` () =
    let actual =
        PlainTextDocumentReader.readText (source (utf8 "Invoice Number: TXT-9001\r\nAmount Due: $42.00\r\nDue 1 March 2026"))

    match actual with
    | Ok lines ->
        Assert.Equal<string list>(
            [ "Invoice Number: TXT-9001"; "Amount Due: $42.00"; "Due 1 March 2026" ],
            lines |> List.map (fun line -> line.Text)
        )
    | Error err -> Assert.Fail($"Expected Ok, got Error {err}")

[<Fact; Trait("Level", "Unit")>]
let ``readText makes a blank line a block boundary and drops the blank`` () =
    // Finding 4: empty lines are dropped before line offsets are applied, and a blank line
    // between blocks is a boundary LinesAfterLabel must respect.
    let actual = PlainTextDocumentReader.readText (source (utf8 "Header line\nSecond line\n\nParagraph after the gap\n"))

    match actual with
    | Ok lines ->
        Assert.Equal<(string * int) list>(
            [ "Header line", 0; "Second line", 0; "Paragraph after the gap", 1 ],
            lines |> List.map (fun line -> line.Text, line.BlockIndex)
        )
    | Error err -> Assert.Fail($"Expected Ok, got Error {err}")

[<Fact; Trait("Level", "Unit")>]
let ``readText collapses consecutive blank lines into one boundary`` () =
    let actual = PlainTextDocumentReader.readText (source (utf8 "one\n\n\n\ntwo"))

    match actual with
    | Ok lines -> Assert.Equal<(string * int) list>([ "one", 0; "two", 1 ], lines |> List.map (fun l -> l.Text, l.BlockIndex))
    | Error err -> Assert.Fail($"Expected Ok, got Error {err}")

[<Fact; Trait("Level", "Unit")>]
let ``readText reports DocumentUnreadable for zero bytes`` () =
    match PlainTextDocumentReader.readText (source [||]) with
    | Error (DocumentUnreadable _) -> ()
    | other -> Assert.Fail($"Expected Error (DocumentUnreadable _), got {other}")
