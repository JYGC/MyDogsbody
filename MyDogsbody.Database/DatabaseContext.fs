namespace MyDogsbody.Database

open Microsoft.Data.Sqlite
open Dapper.FSharp.SQLite
open MyDogsbody.Database.Models

type DatabaseContext =
    {
        GetDatabaseConnection: unit -> SqliteConnection
        GetBlogs: unit -> QuerySource<Blog>
        GetComments: unit -> QuerySource<Comment>
    }