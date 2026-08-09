namespace MyDogsbody.Database

open System
open Microsoft.Data.Sqlite
open Dapper.FSharp.SQLite
open MyDogsbody.Database.Models

type DatabaseContext =
    {
        GetDatabaseConnection: unit -> SqliteConnection
        GetBlogs: unit -> QuerySource<Blog>
        GetComments: unit -> QuerySource<Comment>
        GetSuppliers: unit -> QuerySource<SupplierRecord>
        GetSupplierMatchers: unit -> QuerySource<SupplierMatcherRecord>

        /// Closes the underlying SqliteConnection.
        ///
        /// createDatabaseContext used to open a connection nobody ever closed. Production opens
        /// one context per process and never disposes it; tests dispose every one they open, so
        /// their temp file can actually be deleted afterwards.
        Dispose: unit -> unit
    }

    interface IDisposable with
        member this.Dispose() = this.Dispose()
