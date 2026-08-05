module MyDogsbody.Tests.Spine.Domains.DocumentDomainTests

open Xunit
open MyDogsbody.Builders
open MyDogsbody.Spine.Domains
open MyDogsbody.Spine.Domains.Types

let private handleError = HandleErrorBuilder (fun _ -> ())

[<Fact; Trait("Level", "Unit")>]
let ``getContentSplitByLines joins words sharing a line, ordered left to right`` () =
    // Arrange — deliberately out of reading order
    let document: DocumentContentDomianTypeDto =
        {
            Words =
                [
                    { Text = "World"; Left = 50.0; Bottom = 100.0 }
                    { Text = "Hello"; Left = 10.0; Bottom = 100.0 }
                ]
        }

    // Act
    let result = DocumentDomain.getContentSplitByLines handleError document

    // Assert
    match result with
    | Ok lines -> Assert.Equal<string list>([ "Hello World" ], lines)
    | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")

[<Fact; Trait("Level", "Unit")>]
let ``getContentSplitByLines returns lines from the top of the page downwards`` () =
    // Arrange — a larger Bottom is higher up the page
    let document: DocumentContentDomianTypeDto =
        {
            Words =
                [
                    { Text = "lower"; Left = 10.0; Bottom = 100.0 }
                    { Text = "upper"; Left = 10.0; Bottom = 700.0 }
                ]
        }

    // Act
    let result = DocumentDomain.getContentSplitByLines handleError document

    // Assert
    match result with
    | Ok lines -> Assert.Equal<string list>([ "upper"; "lower" ], lines)
    | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")

[<Fact; Trait("Level", "Unit")>]
let ``getContentSplitByLines groups words within the line tolerance`` () =
    // Arrange — 100.0 and 101.0 fall in the same 2.0-wide band
    let document: DocumentContentDomianTypeDto =
        {
            Words =
                [
                    { Text = "same"; Left = 10.0; Bottom = 100.0 }
                    { Text = "line"; Left = 40.0; Bottom = 101.0 }
                ]
        }

    // Act
    let result = DocumentDomain.getContentSplitByLines handleError document

    // Assert
    match result with
    | Ok lines -> Assert.Equal<string list>([ "same line" ], lines)
    | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")

[<Fact; Trait("Level", "Unit")>]
let ``getContentSplitByLines returns an empty list for a document with no words`` () =
    // Arrange
    let document: DocumentContentDomianTypeDto = { Words = [] }

    // Act
    let result = DocumentDomain.getContentSplitByLines handleError document

    // Assert
    match result with
    | Ok lines -> Assert.Empty(lines)
    | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
