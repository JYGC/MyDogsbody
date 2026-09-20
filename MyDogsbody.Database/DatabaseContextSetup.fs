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
    let invoicesTableName = "Invoices"
    let scanProblemsTableName = "ScanProblems"
    let invoiceTombstonesTableName = "InvoiceTombstones"
    let scanWindowsTableName = "ScanWindows"
    let invoiceSettingsTableName = "InvoiceSettings"
    let invoiceCalendarEventsTableName = "InvoiceCalendarEvents"

    OptionTypes.register()

    // Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Database.md - DatabaseContextSetup.fs: databaseConnection
    let databaseConnection =
        new SqliteConnection($"Data Source={databaseFilePath};Foreign Keys=True;Pooling=False")

    let blogsTable = table'<Blog> blogsTableName
    let commentsTable = table'<Comment> commentsTableName
    let suppliersTable = table'<SupplierRecord> suppliersTableName
    let supplierMatchersTable = table'<SupplierMatcherRecord> supplierMatchersTableName
    let invoiceTemplatesTable = table'<InvoiceTemplateRecord> invoiceTemplatesTableName
    let templateFieldRulesTable = table'<TemplateFieldRuleRecord> templateFieldRulesTableName
    let invoicesTable = table'<InvoiceRecord> invoicesTableName
    let scanProblemsTable = table'<ScanProblemRecord> scanProblemsTableName
    let invoiceTombstonesTable = table'<InvoiceTombstoneRecord> invoiceTombstonesTableName
    let scanWindowsTable = table'<ScanWindowRecord> scanWindowsTableName
    let invoiceSettingsTable = table'<InvoiceSettingsRecord> invoiceSettingsTableName
    let invoiceCalendarEventsTable = table'<InvoiceCalendarEventRecord> invoiceCalendarEventsTableName

    {
        GetDatabaseConnection = fun () -> databaseConnection
        GetBlogs = fun () -> blogsTable
        GetComments = fun () -> commentsTable
        GetSuppliers = fun () -> suppliersTable
        GetSupplierMatchers = fun () -> supplierMatchersTable
        GetInvoiceTemplates = fun () -> invoiceTemplatesTable
        GetTemplateFieldRules = fun () -> templateFieldRulesTable
        GetInvoices = fun () -> invoicesTable
        GetScanProblems = fun () -> scanProblemsTable
        GetInvoiceTombstones = fun () -> invoiceTombstonesTable
        GetScanWindows = fun () -> scanWindowsTable
        GetInvoiceSettings = fun () -> invoiceSettingsTable
        GetInvoiceCalendarEvents = fun () -> invoiceCalendarEventsTable
        Dispose = fun () -> databaseConnection.Dispose()
    }
