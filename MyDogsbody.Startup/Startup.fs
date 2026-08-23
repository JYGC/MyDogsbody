/// The composition root.
///
/// This is the one file in the application that owns process-lifetime resources: the module
/// level bindings below open Logging.db, Credentials.db and MyDogsbody.db - and run the main
/// database's migrations - the moment anything in this module is touched. Nothing else belongs
/// here — everything with behaviour worth testing lives in the *ApiMappers.fs and *ApiFactory.fs
/// files, which this file only partially applies.
///
/// Because they share one module they share one type initializer: if any of them throws, every
/// later access to this module raises TypeInitializationException, not only the one that failed.
/// That was already true of the two LiteDB contexts; the main database adds a third way in.
module MyDogsbody.Startup.Startup

open System
open Microsoft.Extensions.DependencyInjection
open MyDogsbody.Builders
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Logging.Database
open MyDogsbody.Logging.Types
open MyDogsbody.Logging.UseCases
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.UI.Types

let private loggingDatabasePath = "Logging.db"
let private loggingDatabaseConnectionType = "shared"

let private loggingDatabaseContext =
    LoggingDatabaseContextModule.getDatabaseContext
        loggingDatabasePath
        loggingDatabaseConnectionType

/// The log write needs a builder of its own, and it must not be the one below: handing
/// writeLog's own failure back to writeLog would recurse. A no-op breaks that cycle.
let private logWriteHandleError = HandleErrorBuilder ignore

/// Writes a failure to Logging.db. Not called for expected failures — HandleErrorBuilder passes
/// a MyDogsbodyException wrapping an ApplicationException straight through unlogged, and a
/// domain error is translated for the UI without ever entering a handleError block.
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

            ExceptionUseCases.addException
                logWriteHandleError
                loggingDatabaseContext.GetExceptionCollection
                logEntry
            // The one sanctioned Result discard in the application. A failed log write cannot
            // itself be logged, and must not displace the failure being recorded — the user is
            // told about that one, not about the diagnostics that went missing. addException
            // still returns Result so that it can be tested.
            |> ignore
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

let private mainDatabasePath = "MyDogsbody.db"

// Migrations run before the context is created, so a store function can never see a table that
// does not exist yet. This is the main database's first ever caller (friction #11) -
// MigrationSetup.setupMigrations had none before this.
do MigrationSetup.setupMigrations $"Data Source={mainDatabasePath}"

let private mainDatabaseContext = DatabaseContextSetup.createDatabaseContext mainDatabasePath

let supplierApi: SupplierApi =
    SupplierApiFactory.createSupplierApi handleError mainDatabaseContext

let templateApi: TemplateApi =
    TemplateApiFactory.createTemplateApi handleError mainDatabaseContext

let private thunderbirdDatabasePath = "Thunderbird.db"
let private thunderbirdDatabaseConnectionType = "shared"

let private thunderbirdDatabaseContext =
    ThunderbirdDatabaseContextModule.getDatabaseContext
        thunderbirdDatabasePath
        thunderbirdDatabaseConnectionType

let mailAccountApi: MailAccountApi =
    MailAccountApiFactory.createMailAccountApi handleError thunderbirdDatabaseContext

/// The host's entire share of the wiring. Every registration is expressed here, in F#, so
/// MainWindow.xaml.cs states which services exist without stating how they are built.
let registerServices (services: IServiceCollection) : IServiceCollection =
    services
        .AddSingleton<CredentialApi>(credentialApi)
        .AddSingleton<SupplierApi>(supplierApi)
        .AddSingleton<TemplateApi>(templateApi)
        .AddSingleton<MailAccountApi>(mailAccountApi)
