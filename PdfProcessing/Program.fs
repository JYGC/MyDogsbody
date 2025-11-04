open System.Reflection
open MyDogsbody.Builders
open MyDogsbody.Infrastructure
open MyDogsbody.Logging.Repositories
open System.IO

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        printfn "Usage: dotnet run <path-to-pdf>"
        1
    else
        let exeDirPath = Assembly.GetExecutingAssembly().Location |> Path.GetDirectoryName
        let logDbPath = Path.Combine(exeDirPath, "logging.db")
        let logDbConnectionType = "shared"
        let loggingContext = MyDogsbody.Logging.SetupLoggingContext.getLoggingDatabaseContext logDbPath logDbConnectionType
        let handleError = HandleErrorBuilder (fun ex -> ExceptionsRepository.insertLog loggingContext ex)
        argv[0]
        |> DocumentInfrastructure.getPdfObject handleError
        |> Result.bind (DocumentInfrastructure.getContentSplitByLines handleError)
        |> (function
            | Ok lines ->
                for line in lines do
                    printfn "%s" line
            | Error ex ->
                eprintfn "Error: %s" ex.Message
        )
        |> ignore
        0
