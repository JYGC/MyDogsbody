module MyDogsbody.Database.DatabaseContextSetup

open Microsoft.Data.Sqlite
open Dapper.FSharp.SQLite
open MyDogsbody.Database.Models

let createDatabaseContext (databaseFilePath): DatabaseContext =
    let blogsTableName = "Blogs"
    let commentsTableName = "Comments"
    let suppliersTableName = "Suppliers"
    let supplierMatchersTableName = "SupplierMatchers"
    let invoiceTemplatesTableName = "InvoiceTemplates"
    let templateFieldRulesTableName = "TemplateFieldRules"

    OptionTypes.register()

    // "Foreign Keys=True" makes Microsoft.Data.Sqlite issue PRAGMA foreign_keys = 1 itself on
    // every open of this connection - PRAGMAs are per-connection and off by default in SQLite,
    // so without this the SupplierMatchers -> Suppliers cascade (migration ...0002) is decorative.
    //
    // "Pooling=False": the app opens exactly one SqliteConnection per process (Startup.fs) and
    // holds it for the process lifetime, so a connection pool amortises nothing here - it is pure
    // cost. It also caused a test flake: a pooled connection keeps its file handle open after
    // Dispose(), so temp-database harnesses reached for the process-global
    // SqliteConnection.ClearAllPools(), which disposes connections other parallel tests are
    // mid-command on. With pooling off, Dispose() releases the handle immediately and there is
    // nothing global to clear. See docs/changes/sqlite-pool-flake.
    let databaseConnection =
        new SqliteConnection($"Data Source={databaseFilePath};Foreign Keys=True;Pooling=False")

    let blogsTable = table'<Blog> blogsTableName
    let commentsTable = table'<Comment> commentsTableName
    let suppliersTable = table'<SupplierRecord> suppliersTableName
    let supplierMatchersTable = table'<SupplierMatcherRecord> supplierMatchersTableName
    let invoiceTemplatesTable = table'<InvoiceTemplateRecord> invoiceTemplatesTableName
    let templateFieldRulesTable = table'<TemplateFieldRuleRecord> templateFieldRulesTableName

    {
        GetDatabaseConnection = fun () -> databaseConnection
        GetBlogs = fun () -> blogsTable
        GetComments = fun () -> commentsTable
        GetSuppliers = fun () -> suppliersTable
        GetSupplierMatchers = fun () -> supplierMatchersTable
        GetInvoiceTemplates = fun () -> invoiceTemplatesTable
        GetTemplateFieldRules = fun () -> templateFieldRulesTable
        Dispose = fun () -> databaseConnection.Dispose()
    }
