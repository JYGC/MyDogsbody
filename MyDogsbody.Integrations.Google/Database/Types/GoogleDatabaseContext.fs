namespace MyDogsbody.Integrations.Google.Database.Types

open System
open LiteDB
open MyDogsbody.Integrations.Google.Database.Models

type GoogleCredentialsCollection = ILiteCollection<GoogleCredential>

/// The Google integration's private store, handed out as a record of getters.
///
/// The collection getter stops here: `unit -> GoogleCredentialsCollection` is how a store
/// function receives its handle, and it goes no further inward. Nothing in MyDogsbody.Domain
/// names ILiteCollection, LiteDatabase, ObjectId or BsonValue.
///
/// Change #6 adds `GetAccountCollection` to this same record.
type GoogleDatabaseContext =
    {
        GetCredentialCollection: unit -> GoogleCredentialsCollection

        /// Releases the underlying database handle. Windows keeps a LiteDB file locked until this
        /// runs, so without it an integration test can only delete its temp file best-effort.
        /// Production opens one context per process and never disposes it.
        Dispose: unit -> unit
    }

    interface IDisposable with
        member this.Dispose() = this.Dispose()
