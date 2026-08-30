module MyDogsbody.Integrations.Google.Database.GoogleDatabaseContextModule

open LiteDB
open MyDogsbody.Integrations.Google.Database.Types
open MyDogsbody.Integrations.Google.Database.Models

/// Opens the Google integration's LiteDB database and returns its context record.
///
/// This store uses a *local* BsonMapper rather than BsonMapper.Global, for two reasons:
///
///   1. `TrimWhitespace` and `EmptyStringToNull` are switched off, so a secret round-trips
///      byte-for-byte - leading and trailing whitespace included. BsonMapper.Global does not do
///      that: its `TrimWhitespace` defaults to true, which the retired shared store inherited and
///      which silently trimmed every secret. requirements.md's regression clause asks for
///      byte-for-byte, and an OAuth refresh token (change #6) does not survive being trimmed.
///
///   2. A per-store mapper is not shared, so it cannot hit the BsonMapper.Global first-use race
///      that CLAUDE-project.md documents as an intermittent test flake.
///
/// The warm-up CLAUDE-project.md asks for still happens - the entity mapping is built here, on
/// one thread, before the context is handed out - just on this mapper instead of the global one.
let getDatabaseContext databasePath connectionType : GoogleDatabaseContext =
    let credentialCollectionName = "Credentials"

    let mapper = BsonMapper()
    mapper.TrimWhitespace <- false
    mapper.EmptyStringToNull <- false
    mapper.ToDocument(GoogleCredential()) |> ignore

    let liteDatabaseConnectionString = $"Filename={databasePath};connection={connectionType}"
    let dbConnection = new LiteDatabase(liteDatabaseConnectionString, mapper)

    {
        GetCredentialCollection = fun () ->
            dbConnection.GetCollection<GoogleCredential>(credentialCollectionName)

        Dispose = fun () -> dbConnection.Dispose()
    }
