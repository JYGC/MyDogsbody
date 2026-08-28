module MyDogsbody.Tests.Domain.Documents.DocumentsTypesTests

open Xunit
open MyDogsbody.Domain.Documents

[<Fact; Trait("Level", "Unit")>]
let ``DocumentPath.create accepts a path and preserves it exactly`` () =
    // Arrange
    let entered = @"C:\reports\statement 2026-08.pdf"

    // Act
    let actual = DocumentPath.create entered

    // Assert
    match actual with
    | Ok path -> Assert.Equal(entered, DocumentPath.value path)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("\t")>]
let ``DocumentPath.create rejects a missing path with a reason`` (entered: string) =
    // Act
    let actual = DocumentPath.create entered

    // Assert
    match actual with
    | Error reason -> Assert.Equal("Document path must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``DocumentPath.create does not require the file to exist`` () =
    // Arrange - whether a file is there is I/O, and the domain performs none. The adapter finds
    // out and reports DocumentUnreadable.
    let actual = DocumentPath.create @"C:\definitely\not\here.pdf"

    // Assert
    Assert.True(Result.isOk actual)

// --- change #4: the second reading capability, declared beside ReadDocumentContent ---

[<Fact; Trait("Level", "Unit")>]
let ``DocumentSource carries the bytes, name and format it was built from`` () =
    // Arrange - a plain carrier: an attachment lives inside a multi-GB mbox and must not be
    // spilled to a temp file to be read (Q1.11). No validation here; a reader reports what it
    // cannot read.
    let bytes = [| 1uy; 2uy; 3uy |]

    // Act
    let source = { Format = Pdf; Name = "invoice-7422.pdf"; Content = bytes }

    // Assert
    Assert.Equal(Pdf, source.Format)
    Assert.Equal("invoice-7422.pdf", source.Name)
    Assert.Same(bytes, source.Content)

[<Fact; Trait("Level", "Unit")>]
let ``DocumentFormatUnsupported names the format that arrived`` () =
    // The measured mailbox carries 114 .xlsx against 1 .docx; the problem row must say which
    // format, so the question of building a reader for it can later be answered from data.
    let error = DocumentFormatUnsupported "xlsx"

    match error with
    | DocumentFormatUnsupported format -> Assert.Equal("xlsx", format)
    | other -> Assert.Fail($"Expected DocumentFormatUnsupported, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``DocumentHasNoTextLayer is a distinct cause from DocumentUnreadable`` () =
    // A scanned-image PDF (1.6% of the measured PDFs) is a different diagnostic from a file that
    // will not open at all (3.7%): one might warrant OCR later, the other is simply broken.
    Assert.NotEqual<DocumentError>(DocumentHasNoTextLayer, DocumentUnreadable "boom")
