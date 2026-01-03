module MyDogsbody.Integrations.Credentials.Database.CredentialsDatabaseContextModule

open LiteDB
open MyDogsbody.Integrations.Credentials.Database.Types
open MyDogsbody.Integrations.Credentials.Database.Models

let getDatabaseContext databasePath connectionType: CredentialsDatabaseContext =
    let credentialCollectionName = "Credentials"

    let liteDatabaseConnectionString = $"Filename={databasePath};connection={connectionType}"
    let dbConnection = new LiteDatabase(liteDatabaseConnectionString)
    
    {
        GetCredentialCollection = fun () ->
            dbConnection.GetCollection<Credential>(credentialCollectionName);
    }