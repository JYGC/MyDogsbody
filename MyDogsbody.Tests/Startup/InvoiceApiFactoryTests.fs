module MyDogsbody.Tests.Startup.InvoiceApiFactoryTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private handleError = HandleErrorBuilder(fun _ -> ())
let private clock () = DateTime(2026, 6, 15, 12, 0, 0)

/// A real migrated main DB and a real (empty) Thunderbird LiteDB, both disposed. No module-level
/// I/O - the factory takes its dependencies as parameters, so nothing reaches Startup.Startup.
let private withApi (test: InvoiceApi -> unit) =
    let mainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let tbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={mainPath}"
    let mainContext = DatabaseContextSetup.createDatabaseContext mainPath
    let tbContext = ThunderbirdDatabaseContextModule.getDatabaseContext tbPath "direct"
    let api = InvoiceApiFactory.createInvoiceApi handleError clock mainContext tbContext

    try
        test api
    finally
        mainContext.Dispose()
        tbContext.Dispose()
        try File.Delete mainPath with _ -> ()
        try File.Delete tbPath with _ -> ()

let private ok label =
    function
    | Ok v -> v
    | Error(ex: MyDogsbodyException) -> failwith $"{label}: {ex.Message}"

[<Fact; Trait("Level", "Integration")>]
let ``Scan with no mail account selected is refused with a readable alert, nothing logged`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let mainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let tbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={mainPath}"
    let mainContext = DatabaseContextSetup.createDatabaseContext mainPath
    let tbContext = ThunderbirdDatabaseContextModule.getDatabaseContext tbPath "direct"

    try
        let api = InvoiceApiFactory.createInvoiceApi recordingHandleError clock mainContext tbContext

        match api.Scan 14 with
        | Error ex ->
            Assert.Contains("mail account", ex.Message)
            Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
            Assert.Empty logged
        | Ok _ -> Assert.Fail("expected NoAccountSelected")
    finally
        mainContext.Dispose()
        tbContext.Dispose()
        try File.Delete mainPath with _ -> ()
        try File.Delete tbPath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``the getters return empty lists against a fresh ledger`` () =
    withApi (fun api ->
        Assert.Empty(api.GetInvoices 90 |> ok "GetInvoices")
        Assert.Empty(api.GetProblems() |> ok "GetProblems")
        Assert.Empty(api.GetTombstones() |> ok "GetTombstones"))

[<Fact; Trait("Level", "Integration")>]
let ``DeleteInvoice on an id that is not there is a readable alert`` () =
    withApi (fun api ->
        match api.DeleteInvoice "999" with
        | Error ex -> Assert.Contains("no longer in the ledger", ex.Message)
        | Ok _ -> Assert.Fail("expected InvoiceNotFound"))

[<Fact; Trait("Level", "Integration")>]
let ``GetInvoices rejects an out-of-bounds window`` () =
    withApi (fun api ->
        match api.GetInvoices 99999 with
        | Error ex -> Assert.Contains("3650", ex.Message)
        | Ok _ -> Assert.Fail("expected the window to be rejected"))
