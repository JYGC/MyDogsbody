open System
open System.IO
open System.Reflection
open MyDogsbody.Builders
open MyDogsbody.Spine.Domains
open MyDogsbody.Logging.Database
open MyDogsbody.Logging.UseCases
open MyDogsbody.Logging.UseCases.Types
open MyDogsbody.Integrations.Pdf.UseCases

[<EntryPoint>]
let main argv =
    match argv.Length with
    | 0 ->
        printfn "Usage: dotnet run <path-to-pdf>"
        1
    | _ ->
        let exeDirPath = Assembly.GetExecutingAssembly().Location |> Path.GetDirectoryName
        let logDbPath = Path.Combine(exeDirPath, "logging.db")
        let logDbConnectionType = "shared"
        let loggingContext =
            LoggingDatabaseContextModule.getDatabaseContext logDbPath logDbConnectionType
        let handleError =
            HandleErrorBuilder
                (fun ex ->
                    let logEntry: ExceptionUseCaseTypeDto =
                        {
                            Message = ex.Message
                            ActionName = ex.ActionName
                            ExceptionDetails = ex.ToString()
                            CreatedDate = DateTime.Now
                        }
                    ExceptionUseCases.addException loggingContext.GetExceptionCollection logEntry
                )
        argv[0]
        |> DocumentUseCases.getPdfContent
            handleError
        |> Result.bind (DomianTypeMappers.mapPdfContentUseCaseTypeDtoToDocumentContentDomianTypeDto handleError)
        |> Result.bind (DocumentDomain.getContentSplitByLines handleError)
        |> (function
            | Ok lines ->
                for line in lines do
                    printfn "%s" line
            | Error ex ->
                eprintfn "Error: %s" ex.Message
        )
        0
