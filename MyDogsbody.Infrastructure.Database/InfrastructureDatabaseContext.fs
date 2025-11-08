module MyDogsbody.Infrastructure.Database.InfrastructureDatabaseContext

open LiteDB
open MyDogsbody.Infrastructure.Database.Models
open MyDogsbody.Infrastructure.Database.Types

let getInfrastructureDatabaseContext databasePath connectionType: InfrastructureDatabaseContext =
    let exceptionCollectionName = "Exceptions"
    let infrastructureCredentialCollectionName = "InfrastructureCredentials";

    let liteDatabaseConnectionString = $"Filename={databasePath};connection={connectionType}"
    let dbConnection = new LiteDatabase(liteDatabaseConnectionString)
    
    {
        GetExceptionCollection = fun () ->
            dbConnection.GetCollection<ExceptionLog>(exceptionCollectionName);
        GetInfrastructureCredentialCollection = fun () ->
            dbConnection.GetCollection<InfrastructureCredential>(infrastructureCredentialCollectionName)
    }