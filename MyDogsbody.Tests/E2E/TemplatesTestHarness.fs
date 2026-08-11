module MyDogsbody.Tests.E2E.TemplatesTestHarness

open System
open System.IO
open Bunit
open Microsoft.Data.Sqlite
open Microsoft.Extensions.DependencyInjection
open MudBlazor.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

/// Same shape as E2E/SuppliersTestHarness.fs's SuppliersHarness, over TemplateApi instead of
/// SupplierApi - both sit on the same main SQLite database.
type TemplatesHarness(templateApi: TemplateApi, logged: ResizeArray<MyDogsbodyException>) =
    inherit TestContext()

    do
        base.Services.AddMudServices() |> ignore
        base.Services.AddSingleton<TemplateApi>(templateApi) |> ignore
        base.JSInterop.Mode <- JSRuntimeMode.Loose

    /// Whatever handleError was asked to log during the flow. Asserting through this rather than
    /// against a real Logging.db keeps the test off the log database entirely, while still
    /// proving whether a failure was recorded.
    member _.Logged = logged

    member _.Api = templateApi

/// Runs a flow against the real composition root over a real temp SQLite file, schema built by
/// the real migrations: component -> API record -> workflow -> adapter -> file -> back into what
/// the component renders. Seeds one supplier row directly - templates always belong to a
/// supplier, and this level is not exercising SupplierApi's own flow.
let withTemplatesHarness (test: TemplatesHarness -> string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath}"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

    let supplierIdString =
        let connection = context.GetDatabaseConnection()
        connection.Open()
        try
            use command = connection.CreateCommand()
            command.CommandText <- "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30); SELECT last_insert_rowid();"
            string (Convert.ToInt64(command.ExecuteScalar()))
        finally
            connection.Close()

    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add
    let api = TemplateApiFactory.createTemplateApi handleError context
    let harness = new TemplatesHarness(api, logged)

    try
        test harness supplierIdString
    finally
        harness.Dispose()
        context.Dispose()
        SqliteConnection.ClearAllPools()
        File.Delete databaseFilePath

/// The same harness over an API that cannot reach its store, for the failure flows.
let withUnreachableTemplateStoreHarness (test: TemplatesHarness -> string -> unit) =
    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add

    let brokenContext: DatabaseContext =
        {
            GetDatabaseConnection = fun () -> raise (InvalidOperationException "database is gone")
            GetBlogs = fun () -> failwith "not used"
            GetComments = fun () -> failwith "not used"
            GetSuppliers = fun () -> failwith "not used"
            GetSupplierMatchers = fun () -> failwith "not used"
            GetInvoiceTemplates = fun () -> failwith "not used"
            GetTemplateFieldRules = fun () -> failwith "not used"
            Dispose = fun () -> ()
        }

    let api = TemplateApiFactory.createTemplateApi handleError brokenContext
    let harness = new TemplatesHarness(api, logged)

    try
        test harness "1"
    finally
        harness.Dispose()
