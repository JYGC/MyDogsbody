module MyDogsbody.Tests.Integrations.Pdf.Domains.ReadPdfDomainTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Pdf.Domains

/// Records what the builder was asked to log, so the unlogged-failure idiom can be asserted.
let private recordingHandleError () =
    let logged = ResizeArray<MyDogsbodyException>()
    HandleErrorBuilder logged.Add, logged

[<Fact; Trait("Level", "Unit")>]
let ``getPdfContent returns Error without logging when the PDF does not exist`` () =
    // Arrange
    let handleError, logged = recordingHandleError ()
    let missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf")

    // Act
    let result = ReadPdfDomain.getPdfContent handleError missingPath

    // Assert
    match result with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Infrastructure.getPdfObject, ex.ActionName)
        Assert.Equal($"PDF file does not exist: {missingPath}", ex.Message)
        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        // Expected failure: it is returned as a value, never handed to writeLog.
        Assert.Empty(logged)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``getPdfContent returns Error and logs when the file is not a readable PDF`` () =
    // Arrange
    let handleError, logged = recordingHandleError ()
    let corruptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf")
    File.WriteAllText(corruptPath, "this is not a PDF at all")

    try
        // Act
        let result = ReadPdfDomain.getPdfContent handleError corruptPath

        // Assert
        match result with
        | Error ex ->
            Assert.Equal(ActionNames.MyDogsbody.Infrastructure.getPdfObject, ex.ActionName)
            Assert.Equal("Failed to extract content from PDF.", ex.Message)
            Assert.NotNull(ex.InnerException)
            // Unexpected failure: this one is logged.
            Assert.Single(logged) |> ignore
            Assert.Same(ex, logged.[0])
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")
    finally
        File.Delete corruptPath
