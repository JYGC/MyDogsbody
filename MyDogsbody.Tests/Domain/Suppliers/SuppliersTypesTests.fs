module MyDogsbody.Tests.Domain.Suppliers.SuppliersTypesTests

open Xunit
open MyDogsbody.Domain.Suppliers

// Constrained types: holding one is the proof it was validated, so every create gets its own
// test - one accepted value, one rejected value per rule, and the rejection reason asserted.

[<Fact; Trait("Level", "Unit")>]
let ``SupplierName.create accepts a non-empty name and trims surrounding whitespace`` () =
    let actual = SupplierName.create "  Acme Ltd  "

    match actual with
    | Ok name -> Assert.Equal("Acme Ltd", SupplierName.value name)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``SupplierName.create rejects an empty or whitespace-only name`` (entered: string) =
    let actual = SupplierName.create entered

    match actual with
    | Error reason -> Assert.Equal("Supplier name must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``SupplierName.create rejects a name longer than 200 characters`` () =
    let tooLong = System.String('a', 201)

    let actual = SupplierName.create tooLong

    match actual with
    | Error reason -> Assert.Equal("Supplier name must be 200 characters or fewer.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``SupplierName.create accepts a name exactly at the 200 character limit`` () =
    let atLimit = System.String('a', 200)

    let actual = SupplierName.create atLimit

    match actual with
    | Ok name -> Assert.Equal(200, (SupplierName.value name).Length)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``PaymentTermDays.create accepts a value inside the 0 to 365 range`` () =
    let actual = PaymentTermDays.create 30

    match actual with
    | Ok term -> Assert.Equal(30, PaymentTermDays.value term)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``PaymentTermDays.create accepts zero, meaning due on issue`` () =
    let actual = PaymentTermDays.create 0

    match actual with
    | Ok term -> Assert.Equal(0, PaymentTermDays.value term)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(-1)>]
[<InlineData(366)>]
let ``PaymentTermDays.create rejects a value outside the range and names the bound`` (entered: int) =
    let actual = PaymentTermDays.create entered

    match actual with
    | Error reason -> Assert.Equal("Payment term days must be between 0 and 365.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``SupplierId.create accepts a non-empty identifier and preserves it exactly`` () =
    let actual = SupplierId.create "42"

    match actual with
    | Ok id -> Assert.Equal("42", SupplierId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``SupplierId.create rejects a missing identifier with a reason`` (entered: string) =
    let actual = SupplierId.create entered

    match actual with
    | Error reason -> Assert.Equal("Supplier id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- SupplierMatcher ----------

[<Fact; Trait("Level", "Unit")>]
let ``SupplierMatcher.create accepts a sender address containing an at sign`` () =
    let actual = SupplierMatcher.create Sender "billing@acme.example"

    match actual with
    | Ok matcher ->
        Assert.Equal(Sender, SupplierMatcher.kind matcher)
        Assert.Equal("billing@acme.example", SupplierMatcher.value matcher)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``SupplierMatcher.create rejects a sender address with no at sign`` () =
    let actual = SupplierMatcher.create Sender "not-an-address"

    match actual with
    | Error reason -> Assert.Equal("A sender address must contain '@'.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``SupplierMatcher.create accepts a sender domain containing no at sign`` () =
    let actual = SupplierMatcher.create Domain "acme.example"

    match actual with
    | Ok matcher ->
        Assert.Equal(MyDogsbody.Domain.Suppliers.Domain, SupplierMatcher.kind matcher)
        Assert.Equal("acme.example", SupplierMatcher.value matcher)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``SupplierMatcher.create rejects a sender domain containing an at sign`` () =
    let actual = SupplierMatcher.create Domain "billing@acme.example"

    match actual with
    | Error reason ->
        Assert.Equal("A sender domain must not contain '@' - enter a domain, not an address.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``SupplierMatcher.create accepts a non-empty subject pattern`` () =
    let actual = SupplierMatcher.create Subject "your invoice"

    match actual with
    | Ok matcher ->
        Assert.Equal(Subject, SupplierMatcher.kind matcher)
        Assert.Equal("your invoice", SupplierMatcher.value matcher)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``SupplierMatcher.create rejects an empty subject pattern`` (entered: string) =
    let actual = SupplierMatcher.create Subject entered

    match actual with
    | Error reason -> Assert.Equal("A subject pattern must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``SupplierMatcher.create rejects a value longer than 400 characters regardless of kind`` () =
    let tooLong = "@" + System.String('a', 400)

    let actual = SupplierMatcher.create Sender tooLong

    match actual with
    | Error reason -> Assert.Equal("Match values must be 400 characters or fewer.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``a constrained SupplierName value survives create then value unchanged`` () =
    let entered = "Société Générale"

    let actual = entered |> SupplierName.create |> Result.map SupplierName.value

    Assert.Equal(Ok entered, actual)
