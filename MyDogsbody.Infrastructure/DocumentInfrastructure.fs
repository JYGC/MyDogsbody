module MyDogsbody.Infrastructure.DocumentInfrastructure

open System
open MyDogsbody.Exceptions.Types
open MyDogsbody.Infrastructure.Types.DocumentTypes
open MyDogsbody.Builders

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