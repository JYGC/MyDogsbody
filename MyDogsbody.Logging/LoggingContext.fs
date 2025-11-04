module MyDogsbody.Logging.LoggingContext

open MyDogsbody.Logging.Models
open MyDogsbody.Logging.Types
open LiteDB

let getLoggingDatabaseContext databasePath connectionType: LoggingDatabaseContext =
    let exceptionCollectionName = "Exceptions"
    let dbConnection =
        new LiteDatabase($"Filename={databasePath};connection={connectionType}")
    {
        GetExceptionCollection = fun () ->
            dbConnection.GetCollection<ExceptionLog>(exceptionCollectionName)
    }