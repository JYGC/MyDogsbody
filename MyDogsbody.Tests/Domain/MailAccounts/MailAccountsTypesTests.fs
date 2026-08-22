module MyDogsbody.Tests.Domain.MailAccounts.MailAccountsTypesTests

open System
open Xunit
open MyDogsbody.Domain.MailAccounts

// Constrained types: holding one is the proof it was validated, so every create gets its own
// test - one accepted value, one rejected value per rule, and the rejection reason asserted.

[<Fact; Trait("Level", "Unit")>]
let ``ProfileRootPath.create accepts a rooted absolute path`` () =
    let actual = ProfileRootPath.create @"C:\Users\test\Thunderbird\Profiles"

    match actual with
    | Ok path -> Assert.Equal(@"C:\Users\test\Thunderbird\Profiles", ProfileRootPath.value path)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``ProfileRootPath.create rejects an empty or whitespace-only path`` (entered: string) =
    let actual = ProfileRootPath.create entered

    match actual with
    | Error reason -> Assert.Equal("Profile root path must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``ProfileRootPath.create rejects a relative path`` () =
    let actual = ProfileRootPath.create @"Thunderbird\Profiles"

    match actual with
    | Error reason -> Assert.Equal("Profile root path must be an absolute path.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``MailAccountId.create accepts a non-empty identifier and preserves it exactly`` () =
    let actual = MailAccountId.create "account1"

    match actual with
    | Ok id -> Assert.Equal("account1", MailAccountId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``MailAccountId.create rejects a missing identifier with a reason`` (entered: string) =
    let actual = MailAccountId.create entered

    match actual with
    | Error reason -> Assert.Equal("Mail account id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``ScanCutoff.ofStartOfDay truncates the time so 09:00 and 17:00 on the same day give one cutoff`` () =
    let morning = DateTime(2026, 8, 15, 9, 0, 0)
    let evening = DateTime(2026, 8, 15, 17, 0, 0)

    let morningCutoff = ScanCutoff.ofStartOfDay morning
    let eveningCutoff = ScanCutoff.ofStartOfDay evening

    Assert.Equal(ScanCutoff.value morningCutoff, ScanCutoff.value eveningCutoff)
    Assert.Equal(DateTime(2026, 8, 15), ScanCutoff.value morningCutoff)

[<Fact; Trait("Level", "Unit")>]
let ``ScanCutoff.ofStartOfDay distinguishes different days`` () =
    let lateOnOneDay = DateTime(2026, 8, 15, 23, 59, 59)
    let earlyOnTheNext = DateTime(2026, 8, 16, 0, 0, 1)

    let cutoff1 = ScanCutoff.ofStartOfDay lateOnOneDay
    let cutoff2 = ScanCutoff.ofStartOfDay earlyOnTheNext

    Assert.NotEqual(ScanCutoff.value cutoff1, ScanCutoff.value cutoff2)
