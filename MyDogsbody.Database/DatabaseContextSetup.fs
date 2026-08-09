module MyDogsbody.Database.DatabaseContextSetup

open Microsoft.Data.Sqlite
open Dapper.FSharp.SQLite
open MyDogsbody.Database.Models

let createDatabaseContext (databaseFilePath): DatabaseContext =
    let blogsTableName = "Blogs"
    let commentsTableName = "Comments"
    let suppliersTableName = "Suppliers"
    let supplierMatchersTableName = "SupplierMatchers"

    OptionTypes.register()

    // "Foreign Keys=True" makes Microsoft.Data.Sqlite issue PRAGMA foreign_keys = 1 itself on
    // every open of this connection - PRAGMAs are per-connection and off by default in SQLite,
    // so without this the SupplierMatchers -> Suppliers cascade (migration ...0002) is decorative.
    let databaseConnection =
        new SqliteConnection($"Data Source={databaseFilePath};Foreign Keys=True")

    let blogsTable = table'<Blog> blogsTableName
    let commentsTable = table'<Comment> commentsTableName
    let suppliersTable = table'<SupplierRecord> suppliersTableName
    let supplierMatchersTable = table'<SupplierMatcherRecord> supplierMatchersTableName

    {
        GetDatabaseConnection = fun () -> databaseConnection
        GetBlogs = fun () -> blogsTable
        GetComments = fun () -> commentsTable
        GetSuppliers = fun () -> suppliersTable
        GetSupplierMatchers = fun () -> supplierMatchersTable
        Dispose = fun () -> databaseConnection.Dispose()
    }
