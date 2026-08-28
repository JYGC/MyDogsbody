module MyDogsbody.Tests.E2E.InvoicesTestHarness

open System
open System.IO
open Bunit
open Microsoft.Data.Sqlite
open Microsoft.Extensions.DependencyInjection
open MudBlazor.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

type InvoicesHarness
    (invoiceApi: InvoiceApi, scanWindowApi: ScanWindowApi, connectionString: string, logged: ResizeArray<MyDogsbodyException>) =
    inherit TestContext()

    do
        base.Services.AddMudServices() |> ignore
        base.Services.AddSingleton<InvoiceApi>(invoiceApi) |> ignore
        base.Services.AddSingleton<ScanWindowApi>(scanWindowApi) |> ignore
        base.JSInterop.Mode <- JSRuntimeMode.Loose

    member _.InvoiceApi = invoiceApi
    member _.ScanWindowApi = scanWindowApi
    member _.Logged = logged

    /// Runs a raw SQL statement against the same file the APIs use - for seeding rows a flow
    /// then reads back through the real API.
    member _.Exec(sql: string) =
        use connection = new SqliteConnection(connectionString)
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

/// The real composition root over real temp SQLite (schema by the real migrations) and a real
/// empty Thunderbird LiteDB. component -> API record -> workflow -> adapter -> file -> back.
let withInvoicesHarness (test: InvoicesHarness -> unit) =
    let mainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let tbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={mainPath}"
    MigrationSetup.setupMigrations connectionString
    let mainContext = DatabaseContextSetup.createDatabaseContext mainPath
    let tbContext = ThunderbirdDatabaseContextModule.getDatabaseContext tbPath "direct"
    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add
    let clock () = DateTime(2026, 6, 15, 12, 0, 0)
    let invoiceApi = InvoiceApiFactory.createInvoiceApi handleError clock mainContext tbContext
    let scanWindowApi = ScanWindowApiFactory.createScanWindowApi handleError mainContext
    let harness = new InvoicesHarness(invoiceApi, scanWindowApi, connectionString, logged)

    try
        test harness
    finally
        harness.Dispose()
        mainContext.Dispose()
        tbContext.Dispose()
        try File.Delete mainPath with _ -> ()
        try File.Delete tbPath with _ -> ()

/// The same over an API whose store cannot be reached, for the failure flow.
let withUnreachableInvoiceStoreHarness (test: InvoicesHarness -> unit) =
    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add
    let clock () = DateTime(2026, 6, 15, 12, 0, 0)

    let broken: DatabaseContext =
        { GetDatabaseConnection = fun () -> raise (InvalidOperationException "database is gone")
          GetBlogs = fun () -> failwith "x"
          GetComments = fun () -> failwith "x"
          GetSuppliers = fun () -> failwith "x"
          GetSupplierMatchers = fun () -> failwith "x"
          GetInvoiceTemplates = fun () -> failwith "x"
          GetTemplateFieldRules = fun () -> failwith "x"
          GetInvoices = fun () -> failwith "x"
          GetScanProblems = fun () -> failwith "x"
          GetInvoiceTombstones = fun () -> failwith "x"
          GetScanWindows = fun () -> failwith "x"
          GetInvoiceSettings = fun () -> failwith "x"
          Dispose = fun () -> () }

    let tbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let tbContext = ThunderbirdDatabaseContextModule.getDatabaseContext tbPath "direct"
    let invoiceApi = InvoiceApiFactory.createInvoiceApi handleError clock broken tbContext
    let scanWindowApi = ScanWindowApiFactory.createScanWindowApi handleError broken
    let harness = new InvoicesHarness(invoiceApi, scanWindowApi, "", logged)

    try
        test harness
    finally
        harness.Dispose()
        tbContext.Dispose()
        try File.Delete tbPath with _ -> ()
