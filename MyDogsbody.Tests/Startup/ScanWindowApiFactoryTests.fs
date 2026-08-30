module MyDogsbody.Tests.Startup.ScanWindowApiFactoryTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private handleError = HandleErrorBuilder(fun _ -> ())

let private withApi (test: ScanWindowApi -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={path}"
    let context = DatabaseContextSetup.createDatabaseContext path
    let api = ScanWindowApiFactory.createScanWindowApi handleError context

    try
        test api
    finally
        context.Dispose()
        try File.Delete path with _ -> ()

let private ok label =
    function
    | Ok v -> v
    | Error(ex: MyDogsbodyException) -> failwith $"{label}: {ex.Message}"

[<Fact; Trait("Level", "Integration")>]
let ``GetScanWindows returns the seeded five with composed labels`` () =
    withApi (fun api ->
        let windows = api.GetScanWindows() |> ok "GetScanWindows"
        Assert.Equal<int list>([ 7; 14; 30; 90; 180 ], windows |> List.map (fun w -> w.Days))
        Assert.Contains(windows, (fun w -> w.Label = "mail received in the last 14 days")))

[<Fact; Trait("Level", "Integration")>]
let ``AddScanWindow then it appears; DeleteScanWindow then it is gone`` () =
    withApi (fun api ->
        api.AddScanWindow 45 |> ok "AddScanWindow"
        let added = api.GetScanWindows() |> ok "get" |> List.find (fun w -> w.Days = 45)
        api.DeleteScanWindow added.Id |> ok "DeleteScanWindow"
        Assert.DoesNotContain(api.GetScanWindows() |> ok "get", (fun w -> w.Days = 45)))

[<Fact; Trait("Level", "Integration")>]
let ``AddScanWindow refuses a duplicate and an out-of-bounds value with an alert`` () =
    withApi (fun api ->
        match api.AddScanWindow 14 with
        | Error ex -> Assert.Contains("already exists", ex.Message)
        | Ok _ -> Assert.Fail("expected the duplicate to be refused")

        match api.AddScanWindow 5000 with
        | Error ex -> Assert.Contains("3650", ex.Message)
        | Ok _ -> Assert.Fail("expected the out-of-bounds value to be refused"))

[<Fact; Trait("Level", "Integration")>]
let ``deleting down to the last window is refused with a named message`` () =
    withApi (fun api ->
        let windows = api.GetScanWindows() |> ok "get"
        // delete all but one
        for w in windows |> List.take 4 do
            api.DeleteScanWindow w.Id |> ok "DeleteScanWindow"

        let last = api.GetScanWindows() |> ok "get" |> List.exactlyOne
        match api.DeleteScanWindow last.Id with
        | Error ex -> Assert.Contains("last scan window", ex.Message)
        | Ok _ -> Assert.Fail("expected the last window's deletion to be refused"))

[<Fact; Trait("Level", "Integration")>]
let ``GetSelectedScanWindow opens on 14 for a fresh database, then on the last choice`` () =
    withApi (fun api ->
        Assert.Equal(14, api.GetSelectedScanWindow() |> ok "GetSelectedScanWindow")

        api.SelectScanWindow 90 |> ok "SelectScanWindow"
        Assert.Equal(90, api.GetSelectedScanWindow() |> ok "GetSelectedScanWindow")

        // delete the remembered window's row - the resolver falls back to 14
        let ninety = api.GetScanWindows() |> ok "get" |> List.find (fun w -> w.Days = 90)
        api.DeleteScanWindow ninety.Id |> ok "DeleteScanWindow"
        Assert.Equal(14, api.GetSelectedScanWindow() |> ok "GetSelectedScanWindow"))

[<Fact; Trait("Level", "Integration")>]
let ``SelectScanWindow refuses a window that is not in the list`` () =
    withApi (fun api ->
        match api.SelectScanWindow 45 with
        | Error ex -> Assert.Contains("no 45-day scan window", ex.Message)
        | Ok _ -> Assert.Fail("expected a window not in the list to be refused"))
