module MyDogsbody.Infrastructure.PdfDocuments.ReadPdfDocuments

open System.IO
open MyDogsbody.Builders
open MyDogsbody.Domains.Types.DocumentTypes
open MyDogsbody.Exceptions.Types
open UglyToad.PdfPig
open System

let getPdfObject
  (handleError: HandleErrorBuilder)
  (pdfPath: string)
  : Result<DocumentObject, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Infrastructure.getPdfObject
    handleError {
        try
            let! document =
                match File.Exists pdfPath with
                | true ->
                    PdfDocument.Open pdfPath |> Ok
                | false ->
                    let message = sprintf "PDF file does not exist: %s" pdfPath
                    MyDogsbodyException(
                        action,
                        message,
                        ApplicationException(message)
                    ) |> Error
            return {
                GetWords = fun () ->
                    [
                        for page in document.GetPages() do
                            for word in page.GetWords() do
                                yield {
                                    Text = word.Text
                                    Bottom = word.BoundingBox.Bottom
                                    Left = word.BoundingBox.Left
                                }
                    ]
            }
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to extract content from PDF.",
                ex
            )
    }