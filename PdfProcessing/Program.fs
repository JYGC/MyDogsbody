open System.Reflection
open MyDogsbody.Builders
open MyDogsbody.Domains
open MyDogsbody.Infrastructure.Database.Repositories
open MyDogsbody.Infrastructure.Database
open System.IO
open MyDogsbody.Integrations.Pdf.UseCases
open MyDogsbody.Integrations.Pdf.Domains

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
        let loggingContext = InfrastructureDatabaseContext.getInfrastructureDatabaseContext logDbPath logDbConnectionType
        let handleError = HandleErrorBuilder (fun ex -> ExceptionRepository.insertOne loggingContext.GetExceptionCollection ex)
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
        |> ignore
        0
