module MyDogsbody.Tests.Domain.Invoices.InvoicesTypesTests

open Xunit
open MyDogsbody.Domain.Invoices

[<Fact; Trait("Level", "Unit")>]
let ``SourceMessageId.create accepts a non-empty identifier and preserves it exactly`` () =
    let actual = SourceMessageId.create "message-42"

    match actual with
    | Ok id -> Assert.Equal("message-42", SourceMessageId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``SourceMessageId.create rejects a missing identifier with a reason`` (entered: string) =
    let actual = SourceMessageId.create entered

    match actual with
    | Error reason -> Assert.Equal("Source message id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ============================ change #4, task 2.1 ============================

[<Fact; Trait("Level", "Unit")>]
let ``InvoiceReference.create accepts a value and preserves it`` () =
    match InvoiceReference.create "INV-1042" with
    | Ok reference -> Assert.Equal("INV-1042", InvoiceReference.value reference)
    | Error reason -> Assert.Fail($"Expected Ok, got Error {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``InvoiceReference.create rejects an empty reference with a reason`` (entered: string) =
    match InvoiceReference.create entered with
    | Error reason -> Assert.Equal("Invoice reference must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``InvoiceReference.create folds internal whitespace so grouped and unspaced are one value`` () =
    // The natural key (Q5.8): the same invoice printed "1234 5678 90" in a PDF and named
    // "1234567890" in an attachment filename must not become two ledger rows.
    let grouped = InvoiceReference.create "1234 5678 90"
    let unspaced = InvoiceReference.create "1234567890"

    match grouped, unspaced with
    | Ok a, Ok b ->
        Assert.Equal("1234567890", InvoiceReference.value a)
        Assert.Equal(InvoiceReference.value a, InvoiceReference.value b)
        Assert.Equal(a, b)
    | _ -> Assert.Fail("Expected both Ok")

[<Fact; Trait("Level", "Unit")>]
let ``Money.create accepts an amount and currency, upper-casing the code`` () =
    match Money.create 320.50m "aud" with
    | Ok money ->
        Assert.Equal(320.50m, Money.amount money)
        Assert.Equal("AUD", Money.currency money)
    | Error reason -> Assert.Fail($"Expected Ok, got Error {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``Money.create stores a zero or negative amount as found`` () =
    // requirements.md edge case - no lower bound.
    match Money.create -5.00m "AUD" with
    | Ok money -> Assert.Equal(-5.00m, Money.amount money)
    | Error reason -> Assert.Fail($"Expected Ok, got Error {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``Money.create rejects an empty currency with a reason`` (currency: string) =
    match Money.create 10m currency with
    | Error reason -> Assert.Equal("Currency must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``Money.create rejects an implausibly large amount, naming the value`` () =
    let tooBig = Money.MaxAbsAmount + 1m

    match Money.create tooBig "AUD" with
    | Error reason -> Assert.Equal($"Amount {tooBig} is implausibly large.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``InvoiceIssueDate.create accepts a plausible date and drops the time`` () =
    match InvoiceIssueDate.create (System.DateTime(2026, 2, 14, 9, 30, 0)) with
    | Ok issued -> Assert.Equal(System.DateTime(2026, 2, 14), InvoiceIssueDate.value issued)
    | Error reason -> Assert.Fail($"Expected Ok, got Error {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(1899)>]
[<InlineData(3001)>]
let ``InvoiceIssueDate.create rejects an implausible year with a reason`` (year: int) =
    match InvoiceIssueDate.create (System.DateTime(year, 6, 1)) with
    | Error reason -> Assert.Equal("Issue date year must be between 1900 and 3000.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``InvoiceDueDate.create accepts a plausible date and drops the time`` () =
    match InvoiceDueDate.create (System.DateTime(2026, 3, 1, 17, 0, 0)) with
    | Ok due -> Assert.Equal(System.DateTime(2026, 3, 1), InvoiceDueDate.value due)
    | Error reason -> Assert.Fail($"Expected Ok, got Error {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(1899)>]
[<InlineData(3001)>]
let ``InvoiceDueDate.create rejects an implausible year with a reason`` (year: int) =
    match InvoiceDueDate.create (System.DateTime(year, 6, 1)) with
    | Error reason -> Assert.Equal("Due date year must be between 1900 and 3000.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``InvoiceId.create accepts a non-empty id and preserves it`` () =
    match InvoiceId.create "abc123" with
    | Ok id -> Assert.Equal("abc123", InvoiceId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, got Error {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``InvoiceId.create rejects a missing id with a reason`` (entered: string) =
    match InvoiceId.create entered with
    | Error reason -> Assert.Equal("Invoice id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ============================ change #4, task 2.2 ============================

[<Theory; Trait("Level", "Unit")>]
[<InlineData(1)>]
[<InlineData(14)>]
[<InlineData(3650)>]
let ``ScanWindowDays.create accepts a value inside the bounds`` (days: int) =
    match ScanWindowDays.create days with
    | Ok window -> Assert.Equal(days, ScanWindowDays.value window)
    | Error reason -> Assert.Fail($"Expected Ok, got Error {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(0)>]
[<InlineData(-1)>]
let ``ScanWindowDays.create rejects fewer than one day`` (days: int) =
    match ScanWindowDays.create days with
    | Error reason -> Assert.Equal("A scan window must be at least one day.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``ScanWindowDays.create rejects more than 3650 days, naming the bound`` () =
    match ScanWindowDays.create 3651 with
    | Error reason -> Assert.Equal("A scan window must be 3650 days or fewer.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``ScanWindowDays.seeded is exactly the five measured starting windows`` () =
    Assert.Equal<int list>([ 7; 14; 30; 90; 180 ], ScanWindowDays.seeded)

[<Fact; Trait("Level", "Unit")>]
let ``ScanWindowDays.fallback is 14`` () = Assert.Equal(14, ScanWindowDays.fallback)

[<Fact; Trait("Level", "Unit")>]
let ``ScanWindowId.create accepts a non-empty id and rejects a missing one`` () =
    match ScanWindowId.create "w1" with
    | Ok id -> Assert.Equal("w1", ScanWindowId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, got Error {reason}")

    match ScanWindowId.create "" with
    | Error reason -> Assert.Equal("Scan window id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
