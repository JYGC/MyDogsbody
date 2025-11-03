module MyDogsbody.Infrastructure.Documents

open System
open UglyToad.PdfPig
open MyDogsbody.ExceptionTypes
open MyDogsbody.InfrastructureTypes.DocumentTypes
open MyDogsbody.Builders

let getPdfObject
  (pdfPath: string)
  : DocumentObject =
    let action = ActionNames.MyDogsbody.Infrastructure.getPdfObject
    let document = PdfDocument.Open(pdfPath)
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
    {
        getWords = getWords
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
                documentObject.getWords()
                |> Seq.groupBy (fun w -> Math.Round(w.Bottom / epsilon))
                |> Seq.sortByDescending fst
                |> Seq.map (fun (_, words) ->
                    words
                    |> Seq.sortBy (fun w -> w.Left)
                    |> Seq.map (fun w -> w.Text)
                    |> String.concat " "
                )
                |> Seq.toList
        with exn ->
            return! new MyDogsbodyException(
                action,
                "Failed to extract content from PDF.",
                exn
            )
    }