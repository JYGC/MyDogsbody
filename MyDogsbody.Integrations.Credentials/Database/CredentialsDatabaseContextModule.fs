module MyDogsbody.Integrations.Credentials.Database.CredentialsDatabaseContextModule

open LiteDB
open MyDogsbody.Integrations.Credentials.Database.Types
open MyDogsbody.Integrations.Credentials.Database.Models

let getDatabaseContext databasePath connectionType: CredentialsDatabaseContext =
    let credentialCollectionName = "Credentials"

    // Force the entity mapping to be built here, on one thread, before the context is handed
    // out. LiteDB caches it on the global BsonMapper and builds it lazily on first use, so two
    // threads mapping Credential for the first time at once can observe a half-built mapping
    // and silently drop a property - a row round-trips with a null Credentials. The UI calls
    // the API from Async.Start threads, so that race is reachable in production, not only in
    // parallel tests.
    BsonMapper.Global.ToDocument(Credential()) |> ignore

    let liteDatabaseConnectionString = $"Filename={databasePath};connection={connectionType}"
    let dbConnection = new LiteDatabase(liteDatabaseConnectionString)
    
    {
        GetCredentialCollection = fun () ->
            dbConnection.GetCollection<Credential>(credentialCollectionName)

        Dispose = fun () -> dbConnection.Dispose()
    }