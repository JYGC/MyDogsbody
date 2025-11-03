module MyDogsbody.Logging.LoggingContext

open MyDogsbody.Logging.Models
open MyDogsbody.Logging.Types
open LiteDB

let getLoggingDatabaseContext databasePath connectionType: LoggingDatabaseContext =
    let exceptionCollectionName = "Exceptions"
    try
        let dbConnection =
            new LiteDatabase($"Filename={databasePath};connection={connectionType}")
        let getExceptionCollection() =
            dbConnection.GetCollection<ExceptionLog>(exceptionCollectionName)
        {
            GetExceptionCollection = getExceptionCollection
        }
    with ex ->
        printfn "Failed to create LiteDB connection: %s" ex.Message
        reraise()