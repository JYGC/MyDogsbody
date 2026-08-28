module MyDogsbody.Tests.Domain.Invoices.ResolveScanWindowWorkflowTests

open Xunit
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Invoices.ResolveScanWindowWorkflow

let private orFail =
    function
    | Ok value -> value
    | Error reason -> failwith $"test setup: {reason}"

let private window (d: int) : StoredScanWindow =
    { Id = ScanWindowId.create $"w{d}" |> orFail
      Days = ScanWindowDays.create d |> orFail }

let private days (d: int) = ScanWindowDays.create d |> orFail

let private seeded = [ 7; 14; 30; 90; 180 ] |> List.map window

[<Fact; Trait("Level", "Unit")>]
let ``nothing remembered against a seeded store opens on 14`` () =
    let actual = resolveScanWindow seeded None
    Assert.Equal(14, ScanWindowDays.value actual)

[<Fact; Trait("Level", "Unit")>]
let ``a remembered window that still exists opens on it`` () =
    let actual = resolveScanWindow seeded (Some(days 90))
    Assert.Equal(90, ScanWindowDays.value actual)

[<Fact; Trait("Level", "Unit")>]
let ``a remembered window that has since been deleted falls back to 14`` () =
    // The case nobody tries by hand: the number is remembered, its row is gone.
    let actual = resolveScanWindow seeded (Some(days 45))
    Assert.Equal(14, ScanWindowDays.value actual)

[<Fact; Trait("Level", "Unit")>]
let ``remembered deleted and 14 also deleted opens on the shortest remaining`` () =
    let without14 = [ 7; 30; 90; 180 ] |> List.map window
    let actual = resolveScanWindow without14 (Some(days 14))
    Assert.Equal(7, ScanWindowDays.value actual)

[<Fact; Trait("Level", "Unit")>]
let ``nothing remembered and 14 absent opens on the shortest remaining`` () =
    let without14 = [ 30; 90 ] |> List.map window
    let actual = resolveScanWindow without14 None
    Assert.Equal(30, ScanWindowDays.value actual)

[<Fact; Trait("Level", "Unit")>]
let ``an empty store falls back to the 14-day constant`` () =
    // Cannot happen in practice - CannotDeleteLastScanWindow forbids it - but the function is total.
    let actual = resolveScanWindow [] (Some(days 90))
    Assert.Equal(14, ScanWindowDays.value actual)
