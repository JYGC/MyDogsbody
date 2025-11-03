open MyDogsbody.Builders
open MyDogsbody.Infrastructure

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        printfn "Usage: dotnet run <path-to-pdf>"
        1
    else
        argv[0]
        |> Documents.getPdfObject
        |> Documents.getContentSplitByLines (HandleErrorBuilder (fun ex -> ex.ToString() |> System.Console.WriteLine))
        |> (function
            | Ok lines ->
                for line in lines do
                    printfn "%s" line
            | Error ex ->
                eprintfn "Error: %s" ex.Message
        )
        |> ignore
        0
