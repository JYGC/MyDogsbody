module MyDogsbody.Tests.Domain.Invoices.ScanWindowWorkflowsTests

open Xunit
open MyDogsbody.Domain.Invoices

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private window (d: int) : StoredScanWindow =
    { Id = ScanWindowId.create $"w{d}" |> orFail
      Days = ScanWindowDays.create d |> orFail }

let private stored ds = ds |> List.map window

// ============================ AddScanWindowWorkflow (5.1) ============================

[<Fact; Trait("Level", "Unit")>]
let ``addScanWindow adds a value inside the bounds that is not already present`` () =
    let mutable saved = None

    let save: SaveScanWindow =
        fun days ->
            saved <- Some(ScanWindowDays.value days)
            Ok(window (ScanWindowDays.value days))

    match AddScanWindowWorkflow.addScanWindow (fun () -> Ok(stored [ 7; 14; 30 ])) save 45 with
    | Ok result ->
        Assert.Equal(45, ScanWindowDays.value result.Days)
        Assert.Equal(Some 45, saved)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``addScanWindow refuses a duplicate with ScanWindowAlreadyExists carrying the days, store not called`` () =
    let mutable saveCalled = false
    let save: SaveScanWindow = fun _ -> saveCalled <- true; Ok(window 14)

    match AddScanWindowWorkflow.addScanWindow (fun () -> Ok(stored [ 7; 14; 30 ])) save 14 with
    | Error(ScanWindowAlreadyExists 14) -> Assert.False(saveCalled)
    | other -> Assert.Fail($"Expected ScanWindowAlreadyExists 14, got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(0)>]
[<InlineData(3651)>]
let ``addScanWindow refuses an out-of-bounds value, neither store function called`` (days: int) =
    let mutable loadCalled = false
    let mutable saveCalled = false

    let load: LoadScanWindows = fun () -> loadCalled <- true; Ok(stored [ 14 ])
    let save: SaveScanWindow = fun _ -> saveCalled <- true; Ok(window 14)

    match AddScanWindowWorkflow.addScanWindow load save days with
    | Error(ScanWindowInvalid _) ->
        Assert.False(loadCalled)
        Assert.False(saveCalled)
    | other -> Assert.Fail($"Expected ScanWindowInvalid, got {other}")

// ============================ DeleteScanWindowWorkflow (5.2) ============================

[<Fact; Trait("Level", "Unit")>]
let ``deleteScanWindow deletes a window, including a seeded one`` () =
    let mutable deletedId = None

    let delete: DeleteScanWindow =
        fun id ->
            deletedId <- Some(ScanWindowId.value id)
            Ok true

    match DeleteScanWindowWorkflow.deleteScanWindow (fun () -> Ok(stored [ 7; 14 ])) delete "w7" with
    | Ok() -> Assert.Equal(Some "w7", deletedId)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``deleting the last remaining window is refused with CannotDeleteLastScanWindow, store not called`` () =
    let mutable deleteCalled = false
    let delete: DeleteScanWindow = fun _ -> deleteCalled <- true; Ok true

    match DeleteScanWindowWorkflow.deleteScanWindow (fun () -> Ok(stored [ 14 ])) delete "w14" with
    | Error CannotDeleteLastScanWindow -> Assert.False(deleteCalled)
    | other -> Assert.Fail($"Expected CannotDeleteLastScanWindow, got {other}")

// ============================ ListScanWindowsWorkflow (5.3) ============================

[<Fact; Trait("Level", "Unit")>]
let ``listScanWindows returns the windows ascending by days`` () =
    match ListScanWindowsWorkflow.listScanWindows (fun () -> Ok(stored [ 90; 7; 30; 14 ])) () with
    | Ok windows ->
        Assert.Equal<int list>([ 7; 14; 30; 90 ], windows |> List.map (fun w -> ScanWindowDays.value w.Days))
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

// ============================ SelectScanWindowWorkflow (5.3) ============================

[<Fact; Trait("Level", "Unit")>]
let ``selectScanWindow persists a choice that is one of the stored windows`` () =
    let mutable saved = None
    let save: SaveSelectedScanWindow = fun days -> saved <- Some(ScanWindowDays.value days); Ok()

    match SelectScanWindowWorkflow.selectScanWindow (fun () -> Ok(stored [ 7; 14; 30 ])) save 30 with
    | Ok chosen ->
        Assert.Equal(30, ScanWindowDays.value chosen)
        Assert.Equal(Some 30, saved)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``selectScanWindow refuses a window not in the list, store not called`` () =
    let mutable saveCalled = false
    let save: SaveSelectedScanWindow = fun _ -> saveCalled <- true; Ok()

    match SelectScanWindowWorkflow.selectScanWindow (fun () -> Ok(stored [ 7; 14; 30 ])) save 45 with
    | Error(ScanWindowNotFound 45) -> Assert.False(saveCalled)
    | other -> Assert.Fail($"Expected ScanWindowNotFound 45, got {other}")
