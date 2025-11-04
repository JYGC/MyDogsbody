module MyDogsbody.Database.DatabaseContextSetup

open Microsoft.Data.Sqlite
open Dapper.FSharp.SQLite
open MyDogsbody.Database.Models

let createDatabaseContext (databaseFilePath): DatabaseContext =
    let blogsTableName = "Blogs"
    let commentsTableName = "Comments"

    OptionTypes.register()
    let databaseConnection = new SqliteConnection($"Data Source={databaseFilePath}")
    let blogsTable = table'<Blog> blogsTableName
    let commentsTable = table'<Comment> commentsTableName

    let getDatabaseConnection() = databaseConnection
    let getBlogsTable() = blogsTable
    let getCommentsTable() = commentsTable

    {
        GetDatabaseConnection = getDatabaseConnection;
        GetBlogs = getBlogsTable;
        GetComments = getCommentsTable;
    }