module MyDogsbody.Tests.Domain.Documents.ReadDocumentLinesWorkflowTests

open Xunit
open MyDogsbody.Domain.Documents

// The four grouping cases moved here from Spine/Domains/DocumentDomainTests.fs. They lost their
// handleError argument on the way: line grouping is a pure decision, so it belongs in the centre
// and needs no builder, no ActionName and no exception handling.

let private reader (content: DocumentContent) : ReadDocumentContent = fun _ -> Ok content

let private linesOrFail result =
    match result with
    | Ok lines -> lines
    | Error error -> failwith $"Expected Ok, but got Error: {error}"

[<Fact; Trait("Level", "Unit")>]
let ``readDocumentLines joins words sharing a line, ordered left to right`` () =
    // Arrange - deliberately out of order, so the sort is what puts them right
    let content =
        {
            Words =
                [
                    { Text = "world"; Bottom = 100.0; Left = 50.0 }
                    { Text = "Hello"; Bottom = 100.0; Left = 10.0 }
                    { Text = "again"; Bottom = 100.0; Left = 90.0 }
                ]
        }

    // Act
    let actual = ReadDocumentLinesWorkflow.readDocumentLines (reader content) "any.pdf" |> linesOrFail

    // Assert
    Assert.Equal<string list>([ "Hello world again" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``readDocumentLines returns lines from the top of the page downwards`` () =
    // Arrange - PDF coordinates grow upwards, so the highest Bottom is the top line
    let content =
        {
            Words =
                [
                    { Text = "bottom"; Bottom = 10.0; Left = 10.0 }
                    { Text = "top"; Bottom = 200.0; Left = 10.0 }
                    { Text = "middle"; Bottom = 100.0; Left = 10.0 }
                ]
        }

    // Act
    let actual = ReadDocumentLinesWorkflow.readDocumentLines (reader content) "any.pdf" |> linesOrFail

    // Assert
    Assert.Equal<string list>([ "top"; "middle"; "bottom" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``readDocumentLines groups words within the line tolerance`` () =
    // Arrange - 100.0 and 100.5 are the same line; 120.0 is not
    let content =
        {
            Words =
                [
                    { Text = "same"; Bottom = 100.0; Left = 10.0 }
                    { Text = "line"; Bottom = 100.5; Left = 50.0 }
                    { Text = "other"; Bottom = 120.0; Left = 10.0 }
                ]
        }

    // Act
    let actual = ReadDocumentLinesWorkflow.readDocumentLines (reader content) "any.pdf" |> linesOrFail

    // Assert
    Assert.Equal<string list>([ "other"; "same line" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``readDocumentLines returns an empty list for a document with no words`` () =
    // Arrange
    let content = { Words = [] }

    // Act
    let actual = ReadDocumentLinesWorkflow.readDocumentLines (reader content) "any.pdf" |> linesOrFail

    // Assert
    Assert.Empty actual

[<Fact; Trait("Level", "Unit")>]
let ``readDocumentLines rejects an empty path and never reaches the reader`` () =
    // Arrange
    let mutable readerCalled = false

    let recordingReader: ReadDocumentContent =
        fun _ ->
            readerCalled <- true
            Ok { Words = [] }

    // Act
    let actual = ReadDocumentLinesWorkflow.readDocumentLines recordingReader "   "

    // Assert
    Assert.Equal<Result<string list, DocumentError>>(
        Error (DocumentPathInvalid "Document path must not be empty."),
        actual
    )

    Assert.False(readerCalled, "validation failed, so the document must not have been opened")

[<Fact; Trait("Level", "Unit")>]
let ``readDocumentLines returns the reader's failure unchanged`` () =
    // Arrange
    let failingReader: ReadDocumentContent =
        fun _ -> Error (DocumentUnreadable "PDF file does not exist: missing.pdf")

    // Act
    let actual = ReadDocumentLinesWorkflow.readDocumentLines failingReader "missing.pdf"

    // Assert
    Assert.Equal<Result<string list, DocumentError>>(
        Error (DocumentUnreadable "PDF file does not exist: missing.pdf"),
        actual
    )

[<Fact; Trait("Level", "Unit")>]
let ``readDocumentLines hands the reader the validated path`` () =
    // Arrange
    let mutable received = ""

    let recordingReader: ReadDocumentContent =
        fun path ->
            received <- DocumentPath.value path
            Ok { Words = [] }

    // Act
    ReadDocumentLinesWorkflow.readDocumentLines recordingReader @"C:\reports\statement.pdf"
    |> linesOrFail
    |> ignore

    // Assert
    Assert.Equal(@"C:\reports\statement.pdf", received)
