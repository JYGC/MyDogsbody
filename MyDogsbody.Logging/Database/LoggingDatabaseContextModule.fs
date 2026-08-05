module MyDogsbody.Logging.Database.LoggingDatabaseContextModule

open LiteDB
open MyDogsbody.Logging.Database.Models
open MyDogsbody.Logging.Database.Types

let getDatabaseContext databasePath connectionType : LoggingDatabaseContext =
    let exceptionCollectionName = "Exceptions"

    // Build the entity mapping here, on one thread, before the context is handed out. LiteDB
    // caches it on the global BsonMapper and builds it lazily, so two threads mapping
    // ExceptionLog for the first time at once can observe a half-built mapping and silently drop
    // a property. writeLog is reached from whichever thread happened to fail, so that race is
    // reachable in production.
    BsonMapper.Global.ToDocument(ExceptionLog()) |> ignore

    let liteDatabaseConnectionString = $"Filename={databasePath};connection={connectionType}"
    let dbConnection = new LiteDatabase(liteDatabaseConnectionString)

    {
        GetExceptionCollection =
            fun () -> dbConnection.GetCollection<ExceptionLog>(exceptionCollectionName)

        Dispose = fun () -> dbConnection.Dispose()
    }
