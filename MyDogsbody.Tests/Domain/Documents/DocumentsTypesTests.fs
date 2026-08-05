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
