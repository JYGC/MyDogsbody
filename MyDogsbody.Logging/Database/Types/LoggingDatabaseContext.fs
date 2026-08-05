namespace MyDogsbody.Logging.Database.Types

open System
open LiteDB
open MyDogsbody.Logging.Database.Models

type ExceptionCollection = ILiteCollection<ExceptionLog>

/// The log store's handles. Same context-record-of-getters seam as everything else, but its own
/// tier: not an integration's private store, and never the main database.
///
/// One getter per log type. Errors are the only type implemented today; a warning collection
/// would be another getter on this record, not a severity column on the entity.
type LoggingDatabaseContext =
    {
        GetExceptionCollection: unit -> ExceptionCollection

        /// Releases the underlying database handle, so a test's temp file can actually be deleted.
        Dispose: unit -> unit
    }

    interface IDisposable with
        member this.Dispose() = this.Dispose()
