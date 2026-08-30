module MyDogsbody.Tests.E2E.SuppliersTestHarness

open System
open System.IO
open Bunit
open Microsoft.Extensions.DependencyInjection
open MudBlazor.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

/// Same shape as E2E/BlazorTestHarness.fs's CredentialsHarness, over the main SQLite database
/// instead of Credentials.db.
type SuppliersHarness(supplierApi: SupplierApi, logged: ResizeArray<MyDogsbodyException>) =
    inherit TestContext()

    do
        base.Services.AddMudServices() |> ignore
        base.Services.AddSingleton<SupplierApi>(supplierApi) |> ignore
        base.JSInterop.Mode <- JSRuntimeMode.Loose

    /// Whatever handleError was asked to log during the flow. Asserting through this rather than
    /// against a real Logging.db keeps the test off the log database entirely, while still
    /// proving whether a failure was recorded.
    member _.Logged = logged

    member _.Api = supplierApi

/// Runs a flow against the real composition root over a real temp SQLite file, schema built by
/// the real migrations: component -> API record -> workflow -> adapter -> file -> back into what
/// the component renders.
let withSuppliersHarness (test: SuppliersHarness -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Pooling=False"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath
    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add
    let api = SupplierApiFactory.createSupplierApi handleError context
    let harness = new SuppliersHarness(api, logged)

    try
        test harness
    finally
        harness.Dispose()
        context.Dispose()
        try File.Delete databaseFilePath with _ -> ()

/// The same harness over an API that cannot reach its store, for the failure flows.
let withUnreachableSupplierStoreHarness (test: SuppliersHarness -> unit) =
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

    let api = SupplierApiFactory.createSupplierApi handleError brokenContext
    let harness = new SuppliersHarness(api, logged)

    try
        test harness
    finally
        harness.Dispose()
