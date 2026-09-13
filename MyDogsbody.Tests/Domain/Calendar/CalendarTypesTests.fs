module MyDogsbody.Tests.Domain.Calendar.CalendarTypesTests

open Xunit
open MyDogsbody.Domain.Calendar

// Constrained types: holding one is the proof it was validated, so every create gets its own
// test - one accepted value, one rejected value per rule, and the rejection reason asserted.

[<Fact; Trait("Level", "Unit")>]
let ``GoogleAccountId.create accepts a non-empty identifier and preserves it exactly`` () =
    let actual = GoogleAccountId.create "account-1"

    match actual with
    | Ok id -> Assert.Equal("account-1", GoogleAccountId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``GoogleAccountId.create rejects a missing identifier with a reason`` (entered: string) =
    let actual = GoogleAccountId.create entered

    match actual with
    | Error reason -> Assert.Equal("Google account id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``GoogleEmail.create accepts an address containing an at sign and trims it`` () =
    let actual = GoogleEmail.create "  someone@example.com  "

    match actual with
    | Ok email -> Assert.Equal("someone@example.com", GoogleEmail.value email)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``GoogleEmail.create rejects a missing address with a reason`` (entered: string) =
    let actual = GoogleEmail.create entered

    match actual with
    | Error reason -> Assert.Equal("Email address must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``GoogleEmail.create rejects a value with no at sign`` () =
    let actual = GoogleEmail.create "not-an-email"

    match actual with
    | Error reason -> Assert.Equal("Email address must contain '@'.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``CalendarId.create accepts a non-empty identifier and preserves it exactly`` () =
    let actual = CalendarId.create "calendar-1"

    match actual with
    | Ok id -> Assert.Equal("calendar-1", CalendarId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``CalendarId.create rejects a missing identifier with a reason`` (entered: string) =
    let actual = CalendarId.create entered

    match actual with
    | Error reason -> Assert.Equal("Calendar id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``CalendarName.create accepts a non-empty name and preserves it exactly`` () =
    let actual = CalendarName.create "Invoices"

    match actual with
    | Ok name -> Assert.Equal("Invoices", CalendarName.value name)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``CalendarName.create rejects a missing name with a reason`` (entered: string) =
    let actual = CalendarName.create entered

    match actual with
    | Error reason -> Assert.Equal("Calendar name must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
