module MyDogsbody.Tests.Contracts.GetCurrentTimeContractTests

open System
open Xunit
open MyDogsbody.Domain.Invoices

// friction #15 - the clock's contract suite, stated rather than skipped.
//
// GetCurrentTime is a dependency function type, and CLAUDE.md calls those published interfaces
// owing a suite run against the real implementation AND every fake. The real implementation is
// `fun () -> DateTime.Now` (bound in Startup.fs), whose whole nature is to return something
// different each call - so "assert the real side and the fake side agree" has no meaning.
//
// What this suite asserts instead, for the real clock and every fake alike:
//   1. two successive calls are non-decreasing;
//   2. the Kind is what the composition root promises (Local, because it binds DateTime.Now).
// For the real clock only, (3) the value is within a tolerance of DateTime.Now at test time.
//
// The part with ACTUAL LOGIC - the cutoff arithmetic (start-of-day, N days back, the same value
// at 09:00 and 17:00) - is unit-tested against fixed instants in ScanForInvoicesWorkflowTests
// (task 3.1), which is where the behaviour worth testing lives.

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
