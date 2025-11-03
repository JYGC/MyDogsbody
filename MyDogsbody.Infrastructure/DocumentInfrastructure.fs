module MyDogsbody.Infrastructure.DocumentInfrastructure

open System
open System.IO
open UglyToad.PdfPig
open MyDogsbody.Exceptions.Types
open MyDogsbody.Infrastructure.Types.DocumentTypes
open MyDogsbody.Builders

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
            let getWords() =
                [
                    for page in document.GetPages() do
                        for word in page.GetWords() do
                            yield {
                                Text = word.Text
                                Bottom = word.BoundingBox.Bottom
                                Left = word.BoundingBox.Left
                            }
                ]
            return {
                GetWords = getWords
            }
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to extract content from PDF.",
                ex
            )
    }

let getContentSplitByLines
  (handleError: HandleErrorBuilder)
  (documentObject: DocumentObject)
  : Result<string list, MyDogsbodyException> =
    let epsilon = 2.0 // tolerance for Y-coordinate differences (line separation)
    let action = ActionNames.MyDogsbody.Infrastructure.getContentSplitByLines
    handleError {
        try
            let w = 2 / 0
            printfn "%i" w
            return
                documentObject.GetWords()
                |> Seq.groupBy (fun w -> Math.Round(w.Bottom / epsilon))
                |> Seq.sortByDescending fst
                |> Seq.map (fun (_, words) ->
                    words
                    |> Seq.sortBy (fun w -> w.Left)
                    |> Seq.map (fun w -> w.Text)
                    |> String.concat " "
                )
                |> Seq.toList
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to extract content from PDF.",
                ex
            )
    }