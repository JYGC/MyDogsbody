module MyDogsbody.Tests.Startup.InvoiceApiFactoryTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
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
let ``RescanEverything with no mail account selected is the same readable alert as Scan, nothing logged`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let mainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let tbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={mainPath}"
    let mainContext = DatabaseContextSetup.createDatabaseContext mainPath
    let tbContext = ThunderbirdDatabaseContextModule.getDatabaseContext tbPath "direct"

    try
        let api = InvoiceApiFactory.createInvoiceApi recordingHandleError clock mainContext tbContext

        match api.RescanEverything 14 with
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

/// The half of task 16.3 the domain unit suite cannot reach: that the factory's
/// `clearWatermarksForAccount` adapter is bound to the Thunderbird store's *watermarks* collection,
/// and that only `RescanEverything` reaches it. Driven through the real factory, a real migrated
/// main DB and a real LiteDB Thunderbird store.
///
/// No account rows are seeded, so the mail read fails either way - deliberately, because the
/// pre-clear happens BEFORE the read (decision 16) and is exactly what is under test. `Scan`
/// resumes from the watermark and leaves the row; `RescanEverything` discards it first.
[<Fact; Trait("Level", "Integration")>]
let ``RescanEverything deletes the selected account's watermark rows, and Scan leaves them`` () =
    let mainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let tbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={mainPath}"
    let mainContext = DatabaseContextSetup.createDatabaseContext mainPath
    let tbContext = ThunderbirdDatabaseContextModule.getDatabaseContext tbPath "direct"

    try
        let accountId =
            match MailAccountId.create @"C:\profile|account1" with
            | Ok id -> id
            | Error reason -> failwith $"test setup: {reason}"

        let watermark: MailFolderReader.FolderWatermark =
            { SizeBytes = 4096L
              ModifiedAt = DateTime(2026, 6, 1)
              OffsetReached = 4096L
              CutoffReached = DateTime(2026, 6, 1) }

        ThunderbirdStore.saveSelectedMailAccount handleError tbContext.GetSelectedAccountCollection (Some accountId)
        |> ok "saveSelectedMailAccount"

        ThunderbirdStore.saveWatermarkEntry handleError tbContext.GetWatermarksCollection accountId "INBOX" watermark
        |> ok "saveWatermarkEntry"

        let api = InvoiceApiFactory.createInvoiceApi handleError clock mainContext tbContext
        let watermarkRows () = tbContext.GetWatermarksCollection().FindAll() |> Seq.toList

        match api.Scan 14 with
        | Ok _ -> Assert.Fail("expected the mail read to fail - no account rows were seeded")
        | Error _ -> ()

        let afterScan = watermarkRows ()
        Assert.Equal(1, List.length afterScan)
        Assert.Equal("INBOX", afterScan.Head.RelativePath)
        Assert.Equal(4096L, afterScan.Head.OffsetReached)

        match api.RescanEverything 14 with
        | Ok _ -> Assert.Fail("expected the mail read to fail - no account rows were seeded")
        | Error _ -> ()

        Assert.Empty(watermarkRows ())
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
