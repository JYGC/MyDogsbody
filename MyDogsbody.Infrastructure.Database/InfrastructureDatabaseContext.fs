module MyDogsbody.Infrastructure.Database.InfrastructureDatabaseContext

open LiteDB
open MyDogsbody.Infrastructure.Database.Models
open MyDogsbody.Infrastructure.Database.Types

let getInfrastructureDatabaseContext databasePath connectionType: InfrastructureDatabaseContext =
    let exceptionCollectionName = "Exceptions"
    let dbConnection =
        new LiteDatabase($"Filename={databasePath};connection={connectionType}")
    {
        GetExceptionCollection = fun () ->
            dbConnection.GetCollection<ExceptionLog>(exceptionCollectionName)
    }