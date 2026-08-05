/// The documents adapter: the function that satisfies the domain's ReadDocumentContent.
///
/// PdfPig lives here and nowhere else. The workflow that groups words into lines sees a function
/// value, so it needs no file and no library to be tested.
module MyDogsbody.Integrations.Pdf.PdfDocumentReader

open System
open System.IO
open UglyToad.PdfPig
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Documents

let readContent
    (handleError: HandleErrorBuilder)
    (path: DocumentPath)
    : Result<DocumentContent, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Pdf.PdfDocumentReader.readContent
    let pdfPath = DocumentPath.value path

    handleError {
        try
            // A missing file is expected rather than exceptional, so it is wrapped around an
            // ApplicationException: HandleErrorBuilder lets that through unlogged.
            let! document =
                match File.Exists pdfPath with
                | true -> PdfDocument.Open pdfPath |> Ok
                | false ->
                    let message = $"PDF file does not exist: {pdfPath}"
                    MyDogsbodyException(action, message, ApplicationException message) |> Error

            use document = document

            return {
                Words =
                    [
                        for page in document.GetPages() do
                            for word in page.GetWords() do
                                yield
                                    {
                                        Text = word.Text
                                        Bottom = word.BoundingBox.Bottom
                                        Left = word.BoundingBox.Left
                                    }
                    ]
            }
        with ex ->
            return! MyDogsbodyException(action, "Failed to extract content from PDF.", ex)
    }
