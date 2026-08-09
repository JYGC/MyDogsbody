module MyDogsbody.Tests.Domain.Suppliers.AddSupplierWorkflowTests

open Xunit
open MyDogsbody.Domain.Suppliers

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private storedSupplier id name termDays matchers : StoredSupplier =
    {
        Id = SupplierId.create id |> valueOrFail
        Name = SupplierName.create name |> valueOrFail
        PaymentTermDays = PaymentTermDays.create termDays |> valueOrFail
        Matchers = matchers
    }

/// A SaveSupplier that records what it was handed, so "the store was never reached" is
/// assertable rather than assumed.
let private recordingSave () =
    let received = ResizeArray<ValidSupplier>()

    let save: SaveSupplier =
        fun supplier ->
            received.Add supplier

            Ok
                {
                    Id = SupplierId.create "99" |> valueOrFail
                    Name = supplier.Name
                    PaymentTermDays = supplier.PaymentTermDays
                    Matchers = supplier.Matchers
                }

    save, received

let private emptyLoad: LoadSuppliers = fun () -> Ok []

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier saves a valid supplier and returns every stored field`` () =
    let save, received = recordingSave ()

    let entered: UnvalidatedSupplier =
        {
            Name = "  Acme Ltd  "
            PaymentTermDays = 30
            Matchers = [ Domain, "acme.example" ]
        }

    let actual = AddSupplierWorkflow.addSupplier emptyLoad save entered

    match actual with
    | Ok stored ->
        Assert.Equal("99", SupplierId.value stored.Id)
        Assert.Equal("Acme Ltd", SupplierName.value stored.Name)
        Assert.Equal(30, PaymentTermDays.value stored.PaymentTermDays)
        Assert.Equal<string list>([ "acme.example" ], stored.Matchers |> List.map SupplierMatcher.value)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let saved = Assert.Single received
    Assert.Equal("Acme Ltd", SupplierName.value saved.Name)
    Assert.Equal(30, PaymentTermDays.value saved.PaymentTermDays)
    Assert.Equal<MatcherKind list>([ Domain ], saved.Matchers |> List.map SupplierMatcher.kind)

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier accepts a supplier with no match rules`` () =
    let save, received = recordingSave ()

    let entered: UnvalidatedSupplier =
        { Name = "No Rules Yet"; PaymentTermDays = 0; Matchers = [] }

    let actual = AddSupplierWorkflow.addSupplier emptyLoad save entered

    match actual with
    | Ok stored -> Assert.Empty stored.Matchers
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier rejects an empty name and never reaches the store`` () =
    let save, received = recordingSave ()

    let entered: UnvalidatedSupplier =
        { Name = "   "; PaymentTermDays = 30; Matchers = [] }

    let actual = AddSupplierWorkflow.addSupplier emptyLoad save entered

    Assert.Equal(Error (SupplierNameInvalid "Supplier name must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier rejects an out-of-range payment term and never reaches the store`` () =
    let save, received = recordingSave ()

    let entered: UnvalidatedSupplier =
        { Name = "Acme"; PaymentTermDays = 400; Matchers = [] }

    let actual = AddSupplierWorkflow.addSupplier emptyLoad save entered

    Assert.Equal(Error (PaymentTermInvalid "Payment term days must be between 0 and 365."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier rejects an invalid match rule naming the offending rule and never reaches the store`` () =
    let save, received = recordingSave ()

    let entered: UnvalidatedSupplier =
        {
            Name = "Acme"
            PaymentTermDays = 30
            Matchers = [ Sender, "no-at-sign" ]
        }

    let actual = AddSupplierWorkflow.addSupplier emptyLoad save entered

    Assert.Equal(Error (MatcherInvalid "A sender address must contain '@'."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier rejects a name that is already taken and never reaches the store`` () =
    let save, received = recordingSave ()
    let load: LoadSuppliers = fun () -> Ok [ storedSupplier "1" "Acme" 30 [] ]

    let entered: UnvalidatedSupplier =
        { Name = "Acme"; PaymentTermDays = 14; Matchers = [] }

    let actual = AddSupplierWorkflow.addSupplier load save entered

    Assert.Equal(Error (SupplierNameTaken "Acme"), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier treats a name clashing only by case as taken`` () =
    let save, received = recordingSave ()
    let load: LoadSuppliers = fun () -> Ok [ storedSupplier "1" "Acme" 30 [] ]

    let entered: UnvalidatedSupplier =
        { Name = "ACME"; PaymentTermDays = 14; Matchers = [] }

    let actual = AddSupplierWorkflow.addSupplier load save entered

    Assert.Equal(Error (SupplierNameTaken "Acme"), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier treats a name clashing only by surrounding whitespace as taken`` () =
    let save, received = recordingSave ()
    let load: LoadSuppliers = fun () -> Ok [ storedSupplier "1" "Acme" 30 [] ]

    let entered: UnvalidatedSupplier =
        { Name = "  Acme  "; PaymentTermDays = 14; Matchers = [] }

    let actual = AddSupplierWorkflow.addSupplier load save entered

    Assert.Equal(Error (SupplierNameTaken "Acme"), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addSupplier returns the store's failure unchanged`` () =
    let save: SaveSupplier = fun _ -> Error (SupplierStoreFailed "disk full")

    let entered: UnvalidatedSupplier =
        { Name = "Acme"; PaymentTermDays = 30; Matchers = [] }

    let actual = AddSupplierWorkflow.addSupplier emptyLoad save entered

    Assert.Equal(Error (SupplierStoreFailed "disk full"), actual)
