module MyDogsbody.Tests.Integrations.Documents.WordDocumentReaderTests

open System.IO
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Documents
open MyDogsbody.Tests.Fixtures

let private source (fileName: string) (bytes: byte[]) : DocumentSource =
    { Format = Word; Name = fileName; Content = bytes }

[<Fact; Trait("Level", "Integration")>]
let ``readText returns each .docx paragraph as a line in its own block`` () =
    // Arrange - a real .docx with three paragraphs
    let docx =
        DocumentFixtures.docxWithParagraphs
            [ "Invoice Number XERO-1001"; "Amount AUD 320.00"; "Due Date 14 February 2026" ]

    // Act
    let actual = WordDocumentReader.readText (source "invoice.docx" docx)

    // Assert - every field of the success output
    match actual with
    | Ok lines ->
        Assert.Equal<string list>(
            [ "Invoice Number XERO-1001"; "Amount AUD 320.00"; "Due Date 14 February 2026" ],
            lines |> List.map (fun line -> line.Text)
        )
        Assert.Equal<int list>([ 0; 1; 2 ], lines |> List.map (fun line -> line.BlockIndex))
    | Error err -> Assert.Fail($"Expected Ok, but got Error: {err}")

[<Fact; Trait("Level", "Integration")>]
let ``readText reports DocumentFormatUnsupported naming "doc" for a legacy binary Word file`` () =
    // Arrange - Q1.12: .docx only. A .doc must be a listed problem naming the format, never a
    // silent skip - silence looks identical to "this supplier sends nothing" (friction #8).
    let doc = File.ReadAllBytes DocumentFixtures.legacyDoc

    // Act
    let actual = WordDocumentReader.readText (source "invoice.doc" doc)

    // Assert - the exact cause, with the format named
    Assert.Equal(Error(DocumentFormatUnsupported "doc"), actual)

[<Fact; Trait("Level", "Unit")>]
let ``readText reports DocumentUnreadable for bytes that are not a .docx package`` () =
    let actual =
        WordDocumentReader.readText (source "invoice.docx" (System.Text.Encoding.UTF8.GetBytes "not a zip"))

    match actual with
    | Error (DocumentUnreadable _) -> ()
    | other -> Assert.Fail($"Expected Error (DocumentUnreadable _), got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``readText reports DocumentUnreadable for zero bytes`` () =
    let actual = WordDocumentReader.readText (source "invoice.docx" [||])

    match actual with
    | Error (DocumentUnreadable _) -> ()
    | other -> Assert.Fail($"Expected Error (DocumentUnreadable _), got {other}")
