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
    // "Pooling=False": a pooled connection keeps its file handle open after Dispose(), so a
    // temp-database test cannot delete its file - which is what drove the harnesses to the
    // process-global SqliteConnection.ClearAllPools(), disposing connections other parallel tests
    // were mid-command on. With pooling off, Dispose() releases the handle immediately and there
    // is nothing global to clear.
    //
    // This is a trade, not a free win. One SqliteConnection *object* is held for the process
    // lifetime (Startup.fs), but its underlying handle is opened and closed per store operation -
    // explicitly in SupplierStore/TemplateStore's inTransaction, and by Dapper around every
    // SelectAsync on a closed connection - so the pool was amortising real work. Measured on
    // Microsoft.Data.Sqlite 9.0.10, one connection object over 2000 open/query/close cycles:
    // 0.090 ms per cycle pooled, 0.470 ms unpooled (+0.38 ms, 5.2x). A suppliers page load is two
    // of those cycles, so well under a millisecond - invisible in a desktop UI, and worth paying
    // to stop the suite failing ~2 runs in 45. See docs/changes/sqlite-pool-flake.
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
