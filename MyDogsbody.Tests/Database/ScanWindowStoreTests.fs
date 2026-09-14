module MyDogsbody.Tests.Database.ScanWindowStoreTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database
open MyDogsbody.Database.Migrations

let private handleError = HandleErrorBuilder(fun _ -> ())

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private withStore (test: DatabaseContext -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={path}"
    let context = DatabaseContextSetup.createDatabaseContext path

    try
        test context
    finally
        context.Dispose()
        // See InvoiceStoreTests.withLedger - no process-global ClearAllPools() from a test.
        try File.Delete path with _ -> ()

let private days (d: int) = ScanWindowDays.create d |> orFail

[<Fact; Trait("Level", "Integration")>]
let ``the seeded five windows are present after migration`` () =
    withStore (fun context ->
        let windows = ScanWindowStore.getScanWindows handleError context.GetDatabaseConnection context.GetScanWindows () |> orFail

        Assert.Equal<int list>(
            [ 7; 14; 30; 90; 180 ],
            windows |> List.map (fun window -> ScanWindowDays.value window.Days) |> List.sort
        ))

[<Fact; Trait("Level", "Integration")>]
let ``a window can be added and then deleted`` () =
    withStore (fun context ->
        let added = ScanWindowStore.saveScanWindow handleError context.GetDatabaseConnection (days 45) |> orFail
        Assert.Equal(45, ScanWindowDays.value added.Days)

        Assert.True(ScanWindowStore.deleteScanWindow handleError context.GetDatabaseConnection added.Id |> orFail)

        let remaining =
            ScanWindowStore.getScanWindows handleError context.GetDatabaseConnection context.GetScanWindows () |> orFail
            |> List.map (fun window -> ScanWindowDays.value window.Days)

        Assert.DoesNotContain(45, remaining))

[<Fact; Trait("Level", "Integration")>]
let ``the selected window persists as a number and survives its row being deleted`` () =
    withStore (fun context ->
        // fresh database: nothing chosen
        Assert.Equal(None, ScanWindowStore.getSelectedScanWindow handleError context.GetDatabaseConnection () |> orFail)

        ScanWindowStore.saveSelectedScanWindow handleError context.GetDatabaseConnection (days 90) |> orFail
        Assert.Equal(Some 90, ScanWindowStore.getSelectedScanWindow handleError context.GetDatabaseConnection () |> orFail |> Option.map ScanWindowDays.value)

        // saving again overwrites the single row
        ScanWindowStore.saveSelectedScanWindow handleError context.GetDatabaseConnection (days 30) |> orFail
        Assert.Equal(Some 30, ScanWindowStore.getSelectedScanWindow handleError context.GetDatabaseConnection () |> orFail |> Option.map ScanWindowDays.value)

        // delete the 30-day row - the remembered NUMBER is unaffected
        let thirty =
            ScanWindowStore.getScanWindows handleError context.GetDatabaseConnection context.GetScanWindows () |> orFail
            |> List.find (fun window -> ScanWindowDays.value window.Days = 30)

        ScanWindowStore.deleteScanWindow handleError context.GetDatabaseConnection thirty.Id |> orFail
        Assert.Equal(Some 30, ScanWindowStore.getSelectedScanWindow handleError context.GetDatabaseConnection () |> orFail |> Option.map ScanWindowDays.value))

[<Fact; Trait("Level", "Unit")>]
let ``a store failure reports the declared action and preserves the inner exception`` () =
    let boom () : SqliteConnection = raise (InvalidOperationException "down")

    match ScanWindowStore.getScanWindows handleError boom (fun () -> failwith "unused") () with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Database.ScanWindowStore.getScanWindows, caughtException.ActionName)
        Assert.Equal("Failed to retrieve scan windows.", caughtException.Message)
        Assert.IsType<InvalidOperationException>(caughtException.InnerException) |> ignore
    | Ok _ -> Assert.Fail("expected Error")
