// A scratch harness that prints a PDF's text, one line at a time.
//
// Also the composition root shape in miniature: it binds the PdfPig adapter to the
// ReadDocumentContent dependency type, translates the adapter's exception into a domain error on
// the way in, and lets the workflow do the deciding.

open System
open System.IO
open System.Reflection
open MyDogsbody.Builders
open MyDogsbody.Logging.Database
open MyDogsbody.Logging.Types
open MyDogsbody.Logging.UseCases
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Documents

let private describe (error: DocumentError) =
    match error with
    | DocumentPathInvalid reason -> reason
    | DocumentUnreadable message -> message

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

        // The log write needs its own builder: handing writeLog's failure back to writeLog
        // would recurse. See Startup.fs.
        let logWriteHandleError = HandleErrorBuilder ignore

        let handleError =
            HandleErrorBuilder
                (fun ex ->
                    let logEntry: ExceptionLogEntry =
                        {
                            Message = ex.Message
                            ActionName = ex.ActionName
                            ExceptionDetails = ex.ToString()
                            CreatedDate = DateTime.Now
                        }
                    // A failed log write cannot itself be logged - see Startup.fs.
                    ExceptionUseCases.addException
                        logWriteHandleError
                        loggingContext.GetExceptionCollection
                        logEntry
                    |> ignore
                )

        // The adapter becomes a dependency: exception in, domain error out.
        let readDocumentContent: ReadDocumentContent =
            fun path ->
                PdfDocumentReader.readContent handleError path
                |> Result.mapError (fun ex -> DocumentUnreadable ex.Message)

        argv[0]
        |> ReadDocumentLinesWorkflow.readDocumentLines readDocumentContent
        |> (function
            | Ok lines ->
                for line in lines do
                    printfn "%s" line
            | Error error -> eprintfn "Error: %s" (describe error)
        )

        0
