module MyDogsbody.Integrations.Logging.Database.LoggingDatabaseContextModule

open LiteDB
open MyDogsbody.Integrations.Logging.Database.Models
open MyDogsbody.Integrations.Logging.Database.Types

let getDatabaseContext databasePath connectionType: LoggingDatabaseContext =
    let exceptionCollectionName = "Exceptions"

    let liteDatabaseConnectionString = $"Filename={databasePath};connection={connectionType}"
    let dbConnection = new LiteDatabase(liteDatabaseConnectionString)
    
    {
        GetExceptionCollection = fun () ->
            dbConnection.GetCollection<ExceptionLog>(exceptionCollectionName);
    }