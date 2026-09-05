module MyDogsbody.Tests.Contracts.GoogleAccountBoundaryMapperTests

open Xunit
open LiteDB
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database.Models

// The bottom mapping point for the account half of the Google integration, asserted
// field-for-field in both directions - CLAUDE.md: "Every mapper at a ring boundary is asserted
// field-for-field in both directions, with deliberate renames asserted as renames."

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private accountId value = GoogleAccountId.create value |> valueOrFail
let private email value = GoogleEmail.create value |> valueOrFail
let private calendarId value = CalendarId.create value |> valueOrFail

let private registeredAccount id emailValue defaultCalendar needsReauth : RegisteredGoogleAccount =
    {
        Id = accountId id
        EmailAddress = email emailValue
        DefaultInvoiceCalendar = defaultCalendar
        NeedsReauthorisation = needsReauth
    }

// ---------- domain type -> entity ----------

[<Fact; Trait("Level", "Contract")>]
let ``toEntity carries every field of an account with a default calendar chosen`` () =
    let account =
        registeredAccount "507f1f77bcf86cd799439011" "person@gmail.com" (Some (calendarId "cal-1")) true

    let actual = GoogleEntityMappers.toEntity account

    Assert.Equal(ObjectId "507f1f77bcf86cd799439011", actual.Id)
    Assert.Equal("person@gmail.com", actual.EmailAddress)
    Assert.Equal("cal-1", actual.DefaultInvoiceCalendarId)
    Assert.True actual.NeedsReauthorisation

[<Fact; Trait("Level", "Contract")>]
let ``toEntity maps no default calendar to a null field`` () =
    let account = registeredAccount "507f1f77bcf86cd799439011" "person@gmail.com" None false

    let actual = GoogleEntityMappers.toEntity account

    Assert.Null actual.DefaultInvoiceCalendarId
    Assert.False actual.NeedsReauthorisation

// ---------- entity -> domain type ----------

[<Fact; Trait("Level", "Contract")>]
let ``toRegisteredAccount carries every field of an entity with a default calendar back`` () =
    let entity =
        GoogleAccountEntity(
            Id = ObjectId "507f1f77bcf86cd799439011",
            EmailAddress = "person@gmail.com",
            DefaultInvoiceCalendarId = "cal-1",
            NeedsReauthorisation = true
        )

    match GoogleEntityMappers.toRegisteredAccount entity with
    | Ok account ->
        Assert.Equal("507f1f77bcf86cd799439011", GoogleAccountId.value account.Id)
        Assert.Equal("person@gmail.com", GoogleEmail.value account.EmailAddress)
        Assert.Equal(Some (calendarId "cal-1"), account.DefaultInvoiceCalendar)
        Assert.True account.NeedsReauthorisation
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Contract")>]
let ``toRegisteredAccount maps a null default calendar id to None`` () =
    let entity =
        GoogleAccountEntity(
            Id = ObjectId "507f1f77bcf86cd799439011",
            EmailAddress = "person@gmail.com",
            DefaultInvoiceCalendarId = null,
            NeedsReauthorisation = false
        )

    match GoogleEntityMappers.toRegisteredAccount entity with
    | Ok account -> Assert.Equal(None, account.DefaultInvoiceCalendar)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Contract")>]
let ``toRegisteredAccount rejects a row that cannot satisfy the domain's rules`` () =
    let entity =
        GoogleAccountEntity(
            Id = ObjectId "507f1f77bcf86cd799439011",
            EmailAddress = "not-an-email",
            DefaultInvoiceCalendarId = null,
            NeedsReauthorisation = false
        )

    match GoogleEntityMappers.toRegisteredAccount entity with
    | Error reason -> Assert.Equal("Email address must contain '@'.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- round trip ----------

[<Fact; Trait("Level", "Contract")>]
let ``the bottom mapper round trips an account unchanged, including a null default calendar and back`` () =
    let account = registeredAccount "507f1f77bcf86cd799439011" "roundtrip@gmail.com" None true

    let entity = GoogleEntityMappers.toEntity account

    match GoogleEntityMappers.toRegisteredAccount entity with
    | Ok reread -> Assert.Equal(account, reread)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Contract")>]
let ``the bottom mapper round trips a chosen default calendar unchanged`` () =
    let account =
        registeredAccount "507f1f77bcf86cd799439011" "roundtrip@gmail.com" (Some (calendarId "cal-9")) false

    let entity = GoogleEntityMappers.toEntity account

    match GoogleEntityMappers.toRegisteredAccount entity with
    | Ok reread -> Assert.Equal(account, reread)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")
