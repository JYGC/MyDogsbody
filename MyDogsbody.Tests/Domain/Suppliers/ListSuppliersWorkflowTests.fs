module MyDogsbody.Tests.Domain.Suppliers.ListSuppliersWorkflowTests

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

[<Fact; Trait("Level", "Unit")>]
let ``listSuppliers returns every field of every stored supplier`` () =
    let acmeMatcher = SupplierMatcher.create Domain "acme.example" |> valueOrFail

    let load: LoadSuppliers =
        fun () ->
            Ok
                [
                    storedSupplier "1" "Acme" 30 [ acmeMatcher ]
                    storedSupplier "2" "Bravo Corp" 14 []
                ]

    let actual = ListSuppliersWorkflow.listSuppliers load ()

    match actual with
    | Ok [ first; second ] ->
        Assert.Equal("1", SupplierId.value first.Id)
        Assert.Equal("Acme", SupplierName.value first.Name)
        Assert.Equal(30, PaymentTermDays.value first.PaymentTermDays)
        Assert.Equal<SupplierMatcher list>([ acmeMatcher ], first.Matchers)

        Assert.Equal("2", SupplierId.value second.Id)
        Assert.Equal("Bravo Corp", SupplierName.value second.Name)
        Assert.Equal(14, PaymentTermDays.value second.PaymentTermDays)
        Assert.Empty second.Matchers
    | Ok other -> Assert.Fail($"Expected exactly two suppliers, got {List.length other}")
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``listSuppliers returns an empty list for an empty store rather than an error`` () =
    let load: LoadSuppliers = fun () -> Ok []

    let actual = ListSuppliersWorkflow.listSuppliers load ()

    Assert.Equal<Result<StoredSupplier list, SupplierError>>(Ok [], actual)

[<Fact; Trait("Level", "Unit")>]
let ``listSuppliers orders the result by name regardless of the order the dependency returned`` () =
    let load: LoadSuppliers =
        fun () ->
            Ok
                [
                    storedSupplier "3" "Zulu Traders" 0 []
                    storedSupplier "1" "Acme" 30 []
                    storedSupplier "2" "Mid Corp" 14 []
                ]

    let actual = ListSuppliersWorkflow.listSuppliers load ()

    match actual with
    | Ok suppliers ->
        Assert.Equal<string list>(
            [ "Acme"; "Mid Corp"; "Zulu Traders" ],
            suppliers |> List.map (fun s -> SupplierName.value s.Name)
        )
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``listSuppliers returns the store's failure unchanged`` () =
    let load: LoadSuppliers = fun () -> Error (SupplierStoreFailed "database unreachable")

    let actual = ListSuppliersWorkflow.listSuppliers load ()

    Assert.Equal<Result<StoredSupplier list, SupplierError>>(
        Error (SupplierStoreFailed "database unreachable"),
        actual
    )
