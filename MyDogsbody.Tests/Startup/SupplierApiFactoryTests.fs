module MyDogsbody.Tests.Startup.SupplierApiFactoryTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private handleError = HandleErrorBuilder (fun _ -> ())

/// Fresh temp SQLite file per test, schema built by the real migrations, context disposed and
/// the file deleted - no test reaches Startup.Startup.
let private withApi (test: SupplierApi -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Pooling=False"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath
    let api = SupplierApiFactory.createSupplierApi handleError context

    try
        test api
    finally
        context.Dispose()
        try File.Delete databaseFilePath with _ -> ()

let private aSupplier: SupplierUiTypeWithoutId =
    { Name = "Acme"; PaymentTermDays = 30; Matchers = [ { Kind = "Domain"; Value = "acme.example" } ] }

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) ->
        failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

let private errorOrFail label result =
    match result with
    | Error (ex: MyDogsbodyException) -> ex
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

let private single (api: SupplierApi) =
    match api.GetAllSuppliers() |> okOrFail "GetAllSuppliers" with
    | [ only ] -> only
    | other -> failwith $"Expected exactly one supplier, got {List.length other}"

[<Fact; Trait("Level", "Integration")>]
let ``GetAllSuppliers returns an empty list for a fresh database`` () =
    withApi (fun api -> Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers"))

[<Fact; Trait("Level", "Integration")>]
let ``AddSupplier stores every field and assigns an identifier`` () =
    withApi (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"

        let stored = single api
        Assert.False(String.IsNullOrWhiteSpace stored.Id)
        Assert.Equal("Acme", stored.Name)
        Assert.Equal(30, stored.PaymentTermDays)
        Assert.Equal<SupplierMatcherUiType list>([ { Kind = "Domain"; Value = "acme.example" } ], stored.Matchers)
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditSupplier changes the addressed supplier`` () =
    withApi (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"
        let stored = single api

        api.EditSupplier
            {
                Id = stored.Id
                Name = "Acme Renamed"
                PaymentTermDays = 45
                Matchers = [ { Kind = "Sender"; Value = "billing@acme.example" } ]
            }
        |> okOrFail "EditSupplier"

        let reloaded = single api
        Assert.Equal(stored.Id, reloaded.Id)
        Assert.Equal("Acme Renamed", reloaded.Name)
        Assert.Equal(45, reloaded.PaymentTermDays)
        Assert.Equal<SupplierMatcherUiType list>([ { Kind = "Sender"; Value = "billing@acme.example" } ], reloaded.Matchers)
    )

[<Fact; Trait("Level", "Integration")>]
let ``DeleteSupplier removes the supplier`` () =
    withApi (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"
        let stored = single api

        api.DeleteSupplier stored.Id |> okOrFail "DeleteSupplier"

        Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddSupplier rejects an empty name and stores nothing`` () =
    withApi (fun api ->
        let actual = api.AddSupplier { aSupplier with Name = "   " } |> errorOrFail "AddSupplier"

        Assert.Equal(ActionNames.MyDogsbody.Startup.SupplierApi.addSupplier, actual.ActionName)
        Assert.Equal("Supplier name must not be empty.", actual.Message)
        Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddSupplier rejects a name already taken and stores nothing more`` () =
    withApi (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"

        let actual = api.AddSupplier { aSupplier with PaymentTermDays = 14 } |> errorOrFail "AddSupplier"

        Assert.Equal("The supplier name 'Acme' is already in use.", actual.Message)
        Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers") |> ignore
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditSupplier reports not found when no supplier carries that id`` () =
    withApi (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"

        let actual =
            api.EditSupplier { Id = "9999"; Name = "Ghost"; PaymentTermDays = 0; Matchers = [] }
            |> errorOrFail "EditSupplier"

        Assert.Equal("No supplier was found with id '9999'.", actual.Message)
        Assert.Equal("Acme", (single api).Name)
    )

[<Fact; Trait("Level", "Integration")>]
let ``DeleteSupplier reports not found when no supplier carries that id`` () =
    withApi (fun api ->
        let actual = api.DeleteSupplier "9999" |> errorOrFail "DeleteSupplier"

        Assert.Equal(ActionNames.MyDogsbody.Startup.SupplierApi.deleteSupplier, actual.ActionName)
        Assert.Equal("No supplier was found with id '9999'.", actual.Message)
    )

[<Fact; Trait("Level", "Integration")>]
let ``a validation failure is never written to the log`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Pooling=False"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath
    let api = SupplierApiFactory.createSupplierApi recordingHandleError context

    try
        api.AddSupplier { aSupplier with Name = "" } |> ignore
        Assert.Empty logged
    finally
        context.Dispose()
        try File.Delete databaseFilePath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``a store failure reaches the UI as an Error and is written to the log exactly once`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let brokenContext: DatabaseContext =
        {
            GetDatabaseConnection = fun () -> raise (InvalidOperationException "database is gone")
            GetBlogs = fun () -> failwith "not used"
            GetComments = fun () -> failwith "not used"
            GetSuppliers = fun () -> failwith "not used"
            GetSupplierMatchers = fun () -> failwith "not used"
            GetInvoiceTemplates = fun () -> failwith "not used"
            GetTemplateFieldRules = fun () -> failwith "not used"
            GetInvoices = fun () -> failwith "not used"
            GetScanProblems = fun () -> failwith "not used"
            GetInvoiceTombstones = fun () -> failwith "not used"
            GetScanWindows = fun () -> failwith "not used"
            GetInvoiceSettings = fun () -> failwith "not used"
            Dispose = fun () -> ()
        }

    let api = SupplierApiFactory.createSupplierApi recordingHandleError brokenContext

    let actual = api.GetAllSuppliers()

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Startup.SupplierApi.getAllSuppliers, ex.ActionName)
        Assert.Equal("Failed to retrieve all suppliers.", ex.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

    Assert.Single logged |> ignore

[<Fact; Trait("Level", "Integration")>]
let ``a supplier name with non-ASCII characters survives the whole api round trip`` () =
    withApi (fun api ->
        let awkward = "Société Générale"

        api.AddSupplier { aSupplier with Name = awkward } |> okOrFail "AddSupplier"

        Assert.Equal(awkward, (single api).Name)
    )
