module MyDogsbody.Tests.Integrations.Documents.PdfDocumentReaderTests

open System
open System.IO
open Xunit
open UglyToad.PdfPig.Writer
open UglyToad.PdfPig.Fonts.Standard14Fonts
open UglyToad.PdfPig.Core
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Documents

/// Records what the builder was asked to log, so the unlogged-failure idiom can be asserted.
let private recordingHandleError () =
    let logged = ResizeArray<MyDogsbodyException>()
    HandleErrorBuilder logged.Add, logged

let private pathOrFail value =
    match DocumentPath.create value with
    | Ok path -> path
    | Error reason -> failwith reason

/// Writes a real one-page PDF containing the given words, so the success path is exercised
/// against PdfPig rather than assumed. There is no checked-in fixture to drift.
let private writePdf (path: string) (words: string list) =
    let builder = PdfDocumentBuilder()
    let page = builder.AddPage(595.0, 842.0)
    let font = builder.AddStandard14Font(Standard14Font.Helvetica)

    words
    |> List.iteri (fun index word ->
        page.AddText(word, 12.0, PdfPoint(50.0, 700.0 - float index * 20.0), font) |> ignore
    )

    File.WriteAllBytes(path, builder.Build())

let private withTempFile extension (test: string -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{extension}")

    try
        test path
    finally
        try File.Delete path with _ -> ()

[<Fact; Trait("Level", "Unit")>]
let ``readContent returns Error without logging when the file does not exist`` () =
    // Arrange - an expected failure, so it is returned as a value and never logged
    let handleError, logged = recordingHandleError ()
    let missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf")

    // Act
    let actual = PdfDocumentReader.readContent handleError (pathOrFail missingPath)

    // Assert
    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Documents.PdfDocumentReader.readContent, ex.ActionName)
        Assert.Equal($"PDF file does not exist: {missingPath}", ex.Message)
        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Empty logged
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``readContent returns Error and logs when the file is not a readable PDF`` () =
    withTempFile ".pdf" (fun corruptPath ->
        // Arrange
        let handleError, logged = recordingHandleError ()
        File.WriteAllText(corruptPath, "this is not a PDF at all")

        // Act
        let actual = PdfDocumentReader.readContent handleError (pathOrFail corruptPath)

        // Assert
        match actual with
        | Error ex ->
            Assert.Equal(ActionNames.MyDogsbody.Integrations.Documents.PdfDocumentReader.readContent, ex.ActionName)
            Assert.Equal("Failed to extract content from PDF.", ex.Message)
            Assert.NotNull ex.InnerException
            // Unexpected failure: this one is logged.
            Assert.Single logged |> ignore
            Assert.Same(ex, logged.[0])
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")
    )

[<Fact; Trait("Level", "Integration")>]
let ``readContent returns every word of a readable PDF with its coordinates`` () =
    withTempFile ".pdf" (fun pdfPath ->
        // Arrange - the success path, which the previous suite never covered
        let handleError, logged = recordingHandleError ()
        writePdf pdfPath [ "Alpha"; "Beta"; "Gamma" ]

        // Act
        let actual = PdfDocumentReader.readContent handleError (pathOrFail pdfPath)

        // Assert
        match actual with
        | Ok content ->
            Assert.Equal<string list>(
                [ "Alpha"; "Beta"; "Gamma" ],
                content.Words |> List.map (fun word -> word.Text)
            )

            // Coordinates must be real, and the words were written top-down
            Assert.All(content.Words, fun word -> Assert.True(word.Left > 0.0))
            Assert.All(content.Words, fun word -> Assert.True(word.Bottom > 0.0))

            let bottoms = content.Words |> List.map (fun word -> word.Bottom)
            Assert.Equal<float list>(bottoms |> List.sortDescending, bottoms)

            Assert.Empty logged
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

[<Fact; Trait("Level", "Integration")>]
let ``readContent returns no words for a PDF with an empty page`` () =
    withTempFile ".pdf" (fun pdfPath ->
        // Arrange
        let handleError, _ = recordingHandleError ()
        writePdf pdfPath []

        // Act
        let actual = PdfDocumentReader.readContent handleError (pathOrFail pdfPath)

        // Assert
        match actual with
        | Ok content -> Assert.Empty content.Words
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )
