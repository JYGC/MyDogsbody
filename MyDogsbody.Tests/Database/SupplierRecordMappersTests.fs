module MyDogsbody.Tests.Database.SupplierRecordMappersTests

open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Database
open MyDogsbody.Database.Models

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

// ---------- MatcherKind <-> string ----------

[<Theory; Trait("Level", "Unit")>]
[<InlineData(0)>] // Sender
[<InlineData(1)>] // Domain
[<InlineData(2)>] // Subject
let ``every MatcherKind case survives the string round trip`` (caseIndex: int) =
    let allCases =
        Reflection.FSharpType.GetUnionCases(typeof<MatcherKind>)
        |> Array.map (fun case -> Reflection.FSharpValue.MakeUnion(case, [||]) :?> MatcherKind)

    let kind = allCases.[caseIndex]

    let actual =
        kind
        |> SupplierRecordMappers.toMatcherKindString
        |> SupplierRecordMappers.fromMatcherKindString

    Assert.Equal(Ok kind, actual)

[<Fact; Trait("Level", "Unit")>]
let ``toMatcherKindString maps each case to its documented persisted string`` () =
    Assert.Equal("Sender", SupplierRecordMappers.toMatcherKindString Sender)
    Assert.Equal("Domain", SupplierRecordMappers.toMatcherKindString Domain)
    Assert.Equal("Subject", SupplierRecordMappers.toMatcherKindString Subject)

[<Fact; Trait("Level", "Unit")>]
let ``fromMatcherKindString rejects a value no build ever declared`` () =
    let actual = SupplierRecordMappers.fromMatcherKindString "Bogus"

    match actual with
    | Error reason -> Assert.Equal("Stored matcher kind 'Bogus' has no domain equivalent.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- toStoredSupplier ----------

[<Fact; Trait("Level", "Unit")>]
let ``toStoredSupplier carries every field of a row with no matchers`` () =
    let row: SupplierRecord = { Id = 7; Name = "Acme"; PaymentTermDays = 30 }

    let actual = SupplierRecordMappers.toStoredSupplier row []

    match actual with
    | Ok stored ->
        Assert.Equal("7", SupplierId.value stored.Id)
        Assert.Equal("Acme", SupplierName.value stored.Name)
        Assert.Equal(30, PaymentTermDays.value stored.PaymentTermDays)
        Assert.Empty stored.Matchers
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``toStoredSupplier attaches every matcher row, mapped by kind`` () =
    let row: SupplierRecord = { Id = 7; Name = "Acme"; PaymentTermDays = 30 }

    let matcherRows: SupplierMatcherRecord list =
        [
            { Id = 1; SupplierId = 7; Kind = "Domain"; Value = "acme.example" }
            { Id = 2; SupplierId = 7; Kind = "Sender"; Value = "billing@acme.example" }
        ]

    let actual = SupplierRecordMappers.toStoredSupplier row matcherRows

    match actual with
    | Ok stored ->
        Assert.Equal(2, List.length stored.Matchers)
        Assert.Contains(stored.Matchers, fun m -> SupplierMatcher.kind m = Domain && SupplierMatcher.value m = "acme.example")
        Assert.Contains(stored.Matchers, fun m -> SupplierMatcher.kind m = Sender && SupplierMatcher.value m = "billing@acme.example")
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``toStoredSupplier preserves a name containing non-ASCII characters`` () =
    let row: SupplierRecord = { Id = 1; Name = "Société Générale"; PaymentTermDays = 0 }

    let actual = SupplierRecordMappers.toStoredSupplier row []

    match actual with
    | Ok stored -> Assert.Equal("Société Générale", SupplierName.value stored.Name)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Unit")>]
let ``toStoredSupplier rejects a matcher row whose kind no build declares`` () =
    let row: SupplierRecord = { Id = 1; Name = "Acme"; PaymentTermDays = 0 }
    let matcherRows: SupplierMatcherRecord list = [ { Id = 1; SupplierId = 1; Kind = "Bogus"; Value = "x" } ]

    let actual = SupplierRecordMappers.toStoredSupplier row matcherRows

    match actual with
    | Error reason -> Assert.Equal("Stored matcher kind 'Bogus' has no domain equivalent.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- toNewSupplierRecord / toNewMatcherRecord ----------

[<Fact; Trait("Level", "Unit")>]
let ``toNewSupplierRecord carries every field of a valid supplier`` () =
    let supplier: ValidSupplier =
        {
            Name = SupplierName.create "Acme" |> valueOrFail
            PaymentTermDays = PaymentTermDays.create 45 |> valueOrFail
            Matchers = []
        }

    let actual = SupplierRecordMappers.toNewSupplierRecord supplier

    Assert.Equal("Acme", actual.Name)
    Assert.Equal(45, actual.PaymentTermDays)

[<Fact; Trait("Level", "Unit")>]
let ``toNewMatcherRecord carries the supplied supplier id, kind and value`` () =
    let matcher = SupplierMatcher.create Subject "your invoice" |> valueOrFail

    let actual = SupplierRecordMappers.toNewMatcherRecord 7 matcher

    Assert.Equal(7, actual.SupplierId)
    Assert.Equal("Subject", actual.Kind)
    Assert.Equal("your invoice", actual.Value)

// ---------- the bottom mapper round trips a supplier unchanged ----------

[<Fact; Trait("Level", "Unit")>]
let ``the bottom mapper round trips a supplier and its matchers unchanged`` () =
    let supplier: ValidSupplier =
        {
            Name = SupplierName.create "Société Générale" |> valueOrFail
            PaymentTermDays = PaymentTermDays.create 60 |> valueOrFail
            Matchers = [ SupplierMatcher.create Domain "acme.example" |> valueOrFail ]
        }

    let record = SupplierRecordMappers.toNewSupplierRecord supplier
    let stampedRecord = { record with Id = 42 }

    let matcherRecords =
        supplier.Matchers |> List.map (SupplierRecordMappers.toNewMatcherRecord 42)

    let actual = SupplierRecordMappers.toStoredSupplier stampedRecord matcherRecords

    match actual with
    | Ok stored ->
        Assert.Equal("42", SupplierId.value stored.Id)
        Assert.Equal(SupplierName.value supplier.Name, SupplierName.value stored.Name)
        Assert.Equal(PaymentTermDays.value supplier.PaymentTermDays, PaymentTermDays.value stored.PaymentTermDays)
        Assert.Equal<string list>(
            supplier.Matchers |> List.map SupplierMatcher.value,
            stored.Matchers |> List.map SupplierMatcher.value
        )
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

// ---------- toRowId ----------

[<Fact; Trait("Level", "Unit")>]
let ``toRowId parses a well-formed identifier back to its integer row id`` () =
    let id = SupplierId.create "42" |> valueOrFail

    Assert.Equal(42, SupplierRecordMappers.toRowId id)
