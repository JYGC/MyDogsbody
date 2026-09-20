module MyDogsbody.Tests.Contracts.GetCurrentTimeContractTests

open System
open Xunit
open MyDogsbody.Domain.Invoices

// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Tests.md - GetCurrentTimeContractTests.fs: assertClockProperties

/// The shared properties every GetCurrentTime must hold.
let private assertClockProperties (clock: GetCurrentTime) (expectedKind: DateTimeKind) =
    let first = clock ()
    let second = clock ()
    Assert.True(second >= first, "two successive calls must be non-decreasing")
    Assert.Equal(expectedKind, first.Kind)
    Assert.Equal(expectedKind, second.Kind)

/// The real implementation, exactly as Startup.fs binds it.
let private realClock: GetCurrentTime = fun () -> DateTime.Now

/// A fake frozen at one instant - what a workflow unit test supplies.
let private frozenClock (instant: DateTime) : GetCurrentTime = fun () -> instant

/// A fake that advances a second each call - a monotonic stand-in.
let private tickingClock () : GetCurrentTime =
    let mutable ticks = 0L

    fun () ->
        ticks <- ticks + 1L
        DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local).AddSeconds(float ticks)

[<Fact; Trait("Level", "Contract")>]
let ``the real clock holds the shared properties`` () =
    assertClockProperties realClock DateTimeKind.Local

[<Fact; Trait("Level", "Contract")>]
let ``the real clock is within a tolerance of DateTime.Now`` () =
    let observed = realClock ()
    Assert.True(abs (DateTime.Now - observed).TotalSeconds < 5.0)

[<Fact; Trait("Level", "Contract")>]
let ``a frozen fake holds the shared properties`` () =
    assertClockProperties (frozenClock (DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Local))) DateTimeKind.Local

[<Fact; Trait("Level", "Contract")>]
let ``a ticking fake holds the shared properties`` () =
    assertClockProperties (tickingClock ()) DateTimeKind.Local
