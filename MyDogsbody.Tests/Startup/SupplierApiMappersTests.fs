module MyDogsbody.Tests.Startup.SupplierApiMappersTests

open System
open Xunit
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// The top mapping point: domain type <-> MyDogsbody.UI.Types record. Also the error translation
// - the seam the UI's alerts are written from.

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

/// The two inbound mappers return Result because an unrecognised matcher kind is reachable
/// input, not an impossible one - see SupplierApiMappers.toMatcherKind.
let private mappedOrFail (result: Result<'T, SupplierError>) =
    match result with
    | Ok value -> value
    | Error error -> failwith $"Expected the mapping to succeed, but got: {error}"

let private storedSupplier id name termDays matchers : StoredSupplier =
    {
        Id = SupplierId.create id |> valueOrFail
        Name = SupplierName.create name |> valueOrFail
        PaymentTermDays = PaymentTermDays.create termDays |> valueOrFail
        Matchers = matchers
    }

let private anAction = ActionNames.MyDogsbody.Startup.SupplierApi.addSupplier

// ---------- domain <-> UI record ----------

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedSupplier carries every field of the UI record`` () =
    let entered: SupplierUiTypeWithoutId =
        {
            Name = "Acme"
            PaymentTermDays = 30
            Matchers = [ { Kind = "Domain"; Value = "acme.example" } ]
        }

    let actual = SupplierApiMappers.toUnvalidatedSupplier entered |> mappedOrFail

    Assert.Equal("Acme", actual.Name)
    Assert.Equal(30, actual.PaymentTermDays)
    Assert.Equal<(MatcherKind * string) list>([ Domain, "acme.example" ], actual.Matchers)

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedSupplierEdit carries every field of the UI record including the id`` () =
    let entered: SupplierUiType =
        {
            Id = "7"
            Name = "Acme"
            PaymentTermDays = 45
            Matchers = [ { Kind = "Sender"; Value = "billing@acme.example" } ]
        }

    let actual = SupplierApiMappers.toUnvalidatedSupplierEdit entered |> mappedOrFail

    Assert.Equal("7", actual.Id)
    Assert.Equal("Acme", actual.Name)
    Assert.Equal(45, actual.PaymentTermDays)
    Assert.Equal<(MatcherKind * string) list>([ Sender, "billing@acme.example" ], actual.Matchers)

[<Fact; Trait("Level", "Unit")>]
let ``toUiType carries every field of the stored supplier`` () =
    let matcher = SupplierMatcher.create Subject "your invoice" |> valueOrFail
    let stored = storedSupplier "7" "Acme" 30 [ matcher ]

    let actual = SupplierApiMappers.toUiType stored

    Assert.Equal("7", actual.Id)
    Assert.Equal("Acme", actual.Name)
    Assert.Equal(30, actual.PaymentTermDays)
    Assert.Equal<SupplierMatcherUiType list>([ { Kind = "Subject"; Value = "your invoice" } ], actual.Matchers)

[<Fact; Trait("Level", "Unit")>]
let ``a UI record survives the round trip through the domain unchanged`` () =
    let entered: SupplierUiType =
        {
            Id = "7"
            Name = "Société Générale"
            PaymentTermDays = 60
            Matchers = [ { Kind = "Domain"; Value = "acme.example" } ]
        }

    let unvalidated = SupplierApiMappers.toUnvalidatedSupplierEdit entered |> mappedOrFail

    let stored : StoredSupplier =
        {
            Id = SupplierId.create unvalidated.Id |> valueOrFail
            Name = SupplierName.create unvalidated.Name |> valueOrFail
            PaymentTermDays = PaymentTermDays.create unvalidated.PaymentTermDays |> valueOrFail
            Matchers =
                unvalidated.Matchers
                |> List.map (fun (kind, value) -> SupplierMatcher.create kind value |> valueOrFail)
        }

    let actual = SupplierApiMappers.toUiType stored

    Assert.Equal(entered.Id, actual.Id)
    Assert.Equal(entered.Name, actual.Name)
    Assert.Equal(entered.PaymentTermDays, actual.PaymentTermDays)
    Assert.Equal<SupplierMatcherUiType list>(entered.Matchers, actual.Matchers)

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Sender", "billing@acme.example")>]
[<InlineData("Domain", "acme.example")>]
[<InlineData("Subject", "your invoice")>]
let ``every matcher kind string round trips through the domain unchanged`` (kindString: string) (matcherValue: string) =
    let entered: SupplierUiTypeWithoutId =
        { Name = "Acme"; PaymentTermDays = 0; Matchers = [ { Kind = kindString; Value = matcherValue } ] }

    let unvalidated = SupplierApiMappers.toUnvalidatedSupplier entered |> mappedOrFail

    let stored : StoredSupplier =
        {
            Id = SupplierId.create "1" |> valueOrFail
            Name = SupplierName.create unvalidated.Name |> valueOrFail
            PaymentTermDays = PaymentTermDays.create unvalidated.PaymentTermDays |> valueOrFail
            Matchers =
                unvalidated.Matchers
                |> List.map (fun (kind, value) -> SupplierMatcher.create kind value |> valueOrFail)
        }

    let actual = SupplierApiMappers.toUiType stored

    Assert.Equal<SupplierMatcherUiType list>([ { Kind = kindString; Value = matcherValue } ], actual.Matchers)

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedSupplier rejects an unrecognised matcher kind as MatcherInvalid`` () =
    let entered: SupplierUiTypeWithoutId =
        { Name = "Acme"; PaymentTermDays = 30; Matchers = [ { Kind = "Nonsense"; Value = "acme.example" } ] }

    match SupplierApiMappers.toUnvalidatedSupplier entered with
    | Error (MatcherInvalid reason) ->
        Assert.Equal("Matcher kind 'Nonsense' has no domain equivalent.", reason)
    | other -> Assert.Fail($"Expected Error (MatcherInvalid ...), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedSupplierEdit rejects an unrecognised matcher kind as MatcherInvalid`` () =
    let entered: SupplierUiType =
        {
            Id = "7"
            Name = "Acme"
            PaymentTermDays = 30
            Matchers = [ { Kind = "Nonsense"; Value = "acme.example" } ]
        }

    match SupplierApiMappers.toUnvalidatedSupplierEdit entered with
    | Error (MatcherInvalid reason) ->
        Assert.Equal("Matcher kind 'Nonsense' has no domain equivalent.", reason)
    | other -> Assert.Fail($"Expected Error (MatcherInvalid ...), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``toUnvalidatedSupplier reports the first unrecognised kind and stops there`` () =
    let entered: SupplierUiTypeWithoutId =
        {
            Name = "Acme"
            PaymentTermDays = 30
            Matchers =
                [
                    { Kind = "Domain"; Value = "acme.example" }
                    { Kind = "First bad"; Value = "a" }
                    { Kind = "Second bad"; Value = "b" }
                ]
        }

    match SupplierApiMappers.toUnvalidatedSupplier entered with
    | Error (MatcherInvalid reason) ->
        Assert.Equal("Matcher kind 'First bad' has no domain equivalent.", reason)
    | other -> Assert.Fail($"Expected Error (MatcherInvalid ...), but got {other}")

// ---------- error translation ----------

[<Fact; Trait("Level", "Contract")>]
let ``SupplierNameInvalid becomes an unlogged exception carrying the reason and the declared action`` () =
    let actual = SupplierApiMappers.toMyDogsbodyException anAction (SupplierNameInvalid "Supplier name must not be empty.")

    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("Supplier name must not be empty.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``PaymentTermInvalid becomes an unlogged exception carrying the reason`` () =
    let actual = SupplierApiMappers.toMyDogsbodyException anAction (PaymentTermInvalid "Payment term days must be between 0 and 365.")

    Assert.Equal("Payment term days must be between 0 and 365.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``SupplierNameTaken becomes an unlogged exception naming the supplier`` () =
    let actual = SupplierApiMappers.toMyDogsbodyException anAction (SupplierNameTaken "Acme")

    Assert.Equal("The supplier name 'Acme' is already in use.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``MatcherInvalid becomes an unlogged exception carrying the reason`` () =
    let actual = SupplierApiMappers.toMyDogsbodyException anAction (MatcherInvalid "A sender address must contain '@'.")

    Assert.Equal("A sender address must contain '@'.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``SupplierIdInvalid becomes an unlogged exception carrying the reason`` () =
    let actual = SupplierApiMappers.toMyDogsbodyException anAction (SupplierIdInvalid "Supplier id must not be empty.")

    Assert.Equal("Supplier id must not be empty.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``SupplierNotFound becomes an unlogged exception naming the identifier`` () =
    let id = SupplierId.create "7" |> valueOrFail

    let actual = SupplierApiMappers.toMyDogsbodyException anAction (SupplierNotFound id)

    Assert.Equal("No supplier was found with id '7'.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``SupplierStoreFailed carries the store's message and is left unmarked, having already been logged by the adapter`` () =
    let actual = SupplierApiMappers.toMyDogsbodyException anAction (SupplierStoreFailed "database is locked")

    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("database is locked", actual.Message)
    Assert.Null actual.InnerException

[<Fact; Trait("Level", "Contract")>]
let ``every SupplierError case produces a non-empty message`` () =
    let id = SupplierId.create "7" |> valueOrFail

    let allCases: SupplierError list =
        [
            SupplierNameInvalid "a"
            PaymentTermInvalid "b"
            SupplierNameTaken "c"
            MatcherInvalid "d"
            SupplierIdInvalid "e"
            SupplierNotFound id
            SupplierStoreFailed "f"
        ]

    let declaredCases = Reflection.FSharpType.GetUnionCases(typeof<SupplierError>) |> Array.length
    Assert.Equal(declaredCases, List.length allCases)

    for case in allCases do
        let actual = SupplierApiMappers.toMyDogsbodyException anAction case
        Assert.False(String.IsNullOrWhiteSpace actual.Message, $"{case} produced an empty message")
        Assert.Equal(anAction, actual.ActionName)

[<Fact; Trait("Level", "Contract")>]
let ``an adapter exception becomes SupplierStoreFailed carrying its message`` () =
    let adapterFailure =
        MyDogsbody.Exceptions.Types.MyDogsbodyException(
            ActionNames.MyDogsbody.Database.SupplierStore.getAll,
            "Failed to retrieve all suppliers.",
            InvalidOperationException "disk gone"
        )

    let actual = SupplierApiMappers.toSupplierError adapterFailure

    Assert.Equal(SupplierStoreFailed "Failed to retrieve all suppliers.", actual)

[<Fact; Trait("Level", "Contract")>]
let ``the inbound and outbound translations round trip an adapter message`` () =
    let adapterFailure =
        MyDogsbody.Exceptions.Types.MyDogsbodyException(
            ActionNames.MyDogsbody.Database.SupplierStore.insertOne,
            "Failed to insert new supplier.",
            InvalidOperationException "disk gone"
        )

    let actual =
        adapterFailure
        |> SupplierApiMappers.toSupplierError
        |> SupplierApiMappers.toMyDogsbodyException anAction

    Assert.Equal("Failed to insert new supplier.", actual.Message)
