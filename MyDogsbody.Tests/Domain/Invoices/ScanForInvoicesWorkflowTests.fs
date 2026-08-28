module MyDogsbody.Tests.Domain.Invoices.ScanForInvoicesWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Invoices.ScanForInvoicesWorkflow

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private window (d: int) = ScanWindowDays.create d |> orFail
let private clock (instant: DateTime) : GetCurrentTime = fun () -> instant

// ---- task 3.1: cutoff arithmetic, fixed clock, exact dates ----

[<Fact; Trait("Level", "Unit")>]
let ``14 days back from a fixed date is an exact date at the start of the day`` () =
    let cutoff = computeCutoff (clock (DateTime(2026, 1, 20, 13, 45, 0))) (window 14)
    Assert.Equal(DateTime(2026, 1, 6), ScanCutoff.value cutoff)

[<Fact; Trait("Level", "Unit")>]
let ``the same window at 09:00 and 17:00 on one day gives the same cutoff`` () =
    let morning = computeCutoff (clock (DateTime(2026, 6, 10, 9, 0, 0))) (window 30)
    let evening = computeCutoff (clock (DateTime(2026, 6, 10, 17, 0, 0))) (window 30)
    Assert.Equal(ScanCutoff.value morning, ScanCutoff.value evening)
    Assert.Equal(DateTime(2026, 5, 11), ScanCutoff.value morning)

[<Fact; Trait("Level", "Unit")>]
let ``180 days back crosses a year boundary correctly`` () =
    let cutoff = computeCutoff (clock (DateTime(2026, 3, 1))) (window 180)
    // 2026-03-01 minus 180 days
    Assert.Equal(DateTime(2026, 3, 1).AddDays(-180.0), ScanCutoff.value cutoff)
    Assert.Equal(2025, (ScanCutoff.value cutoff).Year)

[<Theory; Trait("Level", "Unit")>]
[<InlineData(1)>]
[<InlineData(3650)>]
let ``a one-day and a ten-year window both land on the start of the day that many days back`` (d: int) =
    let now = DateTime(2026, 7, 4, 23, 59, 0)
    let cutoff = computeCutoff (clock now) (window d)
    Assert.Equal(now.Date.AddDays(float -d), ScanCutoff.value cutoff)
    Assert.Equal(TimeSpan.Zero, (ScanCutoff.value cutoff).TimeOfDay)
