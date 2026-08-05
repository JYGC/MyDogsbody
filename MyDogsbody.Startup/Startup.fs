/// The composition root.
///
/// This is the one file in the application that owns process-lifetime resources: the module
/// level bindings below open Logging.db and Credentials.db the moment anything in this module
/// is touched. Nothing else belongs here — everything with behaviour worth testing lives in
/// CredentialApiMappers.fs and CredentialApiFactory.fs, which this file only partially applies.
module MyDogsbody.Startup.Startup

open System
open Microsoft.Extensions.DependencyInjection
open MyDogsbody.Builders
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Logging.Database
open MyDogsbody.Logging.UseCases
open MyDogsbody.Logging.UseCases.Types
open MyDogsbody.UI.Types

let private loggingDatabasePath = "Logging.db"
let private loggingDatabaseConnectionType = "shared"

let private loggingDatabaseContext =
    LoggingDatabaseContextModule.getDatabaseContext
        loggingDatabasePath
        loggingDatabaseConnectionType

/// Writes a failure to Logging.db. Not called for expected failures — HandleErrorBuilder
/// passes a MyDogsbodyException wrapping an ApplicationException straight through unlogged.
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
            ExceptionUseCases.addException
                loggingDatabaseContext.GetExceptionCollection
                logEntry
        )

let private credentialDatabasePath = "Credentials.db"
let private credentialDatabaseConnectionType = "shared"

let private credentialDatabaseContext =
    CredentialsDatabaseContextModule.getDatabaseContext
        credentialDatabasePath
        credentialDatabaseConnectionType

let credentialApi: CredentialApi =
    CredentialApiFactory.createCredentialApi
        handleError
        credentialDatabaseContext.GetCredentialCollection

/// The host's entire share of the wiring. Every registration is expressed here, in F#, so
/// MainWindow.xaml.cs states which services exist without stating how they are built.
let registerServices (services: IServiceCollection) : IServiceCollection =
    services.AddSingleton<CredentialApi>(credentialApi)
