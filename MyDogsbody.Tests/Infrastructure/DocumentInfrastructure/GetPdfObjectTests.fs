module MyDogsbody.Tests.Infrastructure.DocumentInfrastructure.GetPdfObjectTests

open System
open System.IO
open Xunit
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domains.Types.DocumentTypes
open MyDogsbody.Builders
open MyDogsbody.Infrastructure.PdfDocuments.ReadPdfDocuments

let handleError = HandleErrorBuilder (fun _ -> ())

[<Fact>]
let ``getPdfObject returns Error when PDF does not exist`` () =
  // Arrange
  let fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf")

  // Act
  let result = getPdfObject handleError fakePath

  // Assert
  match result with
  | Error ex ->
      Assert.IsType<MyDogsbodyException>(ex) |> ignore
      Assert.Contains("PDF file does not exist", ex.Message)
  | Ok _ ->
      Assert.Fail("Expected Error, but got Ok")


[<Fact>]
let ``getPdfObject returns Ok when valid PDF exists`` () =
  // Arrange
  let tempPdfPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf")

  // Create a very minimal PDF file
  File.WriteAllText(tempPdfPath, """
%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>
endobj
xref
0 4
0000000000 65535 f 
0000000010 00000 n 
0000000060 00000 n 
0000000117 00000 n 
trailer
<< /Root 1 0 R /Size 4 >>
startxref
176
%%EOF
""")

  // Act
  let result = getPdfObject handleError tempPdfPath

  // Assert
  match result with
  | Ok doc ->
      let words = doc.GetWords()
      Assert.NotNull(words)
  | Error ex ->
      Assert.Fail($"Expected Ok, but got Error: {ex.Message}")

  // Cleanup
  File.Delete tempPdfPath