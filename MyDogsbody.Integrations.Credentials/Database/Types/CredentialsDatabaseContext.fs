namespace MyDogsbody.Integrations.Credentials.Database.Types

open System
open LiteDB
open MyDogsbody.Integrations.Credentials.Database.Models

type CredentialsCollection = ILiteCollection<Credential>

/// The integration's private store, handed out as a record of getters.
///
/// The collection getter stops here: `unit -> CredentialsCollection` is how a store function
/// receives its handle, and it goes no further inward. MyDogsbody.Domain never names
/// ILiteCollection, LiteDatabase, ObjectId or BsonValue - what it declares is a function type,
/// and CredentialStore's job is to satisfy one.
type CredentialsDatabaseContext =
    {
        GetCredentialCollection: unit -> CredentialsCollection

        /// Releases the underlying database handle.
        ///
        /// Windows keeps a LiteDB file locked until this runs, so without it an integration test
        /// can only delete its temp file on a best-effort basis. Production opens one context per
        /// process and never disposes it; tests dispose every one they open.
        Dispose: unit -> unit
    }

    interface IDisposable with
        member this.Dispose() = this.Dispose()
