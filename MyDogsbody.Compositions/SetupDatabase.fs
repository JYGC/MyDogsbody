module MyDogsbody.Compositions.SetupDatabases

open System
open MyDogsbody.Builders
open MyDogsbody.Integrations.Logging.Database
open MyDogsbody.Integrations.Logging.UseCases
open MyDogsbody.Integrations.Logging.UseCases.Types
open MyDogsbody.Integrations.Credentials.Database

let loggingDatabasePath = "Logging.db"
let loggingDatabaseConnectionType = "shared"

let infrastructureDatabaseContext =
    LoggingDatabaseContextModule.getDatabaseContext
        loggingDatabasePath
        loggingDatabaseConnectionType

let handleError =
    HandleErrorBuilder
        (fun ex ->
            {
                Message = ex.Message
                ActionName = ex.ActionName
                ExceptionDetails = ex.ToString()
                CreatedDate = DateTime.Now
            }
            |> ExceptionUseCases.addException
                infrastructureDatabaseContext.GetExceptionCollection
            |> ignore
        )

let credentialDatabasePath = "Credentials.db"
let credentialDatabaseConnectionType = "shared"

let credentialDatabaseContext =
    CredentialsDatabaseContextModule.getDatabaseContext
        credentialDatabasePath
        credentialDatabaseConnectionType