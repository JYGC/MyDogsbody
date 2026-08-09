module MyDogsbody.Tests.Domain.Suppliers.DeleteSupplierWorkflowTests

open Xunit
open MyDogsbody.Domain.Suppliers

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

/// Records every delete attempt, so "the store was never reached" is assertable.
let private recordingDelete (outcome: SupplierId -> Result<bool, SupplierError>) =
    let received = ResizeArray<SupplierId>()

    let delete: DeleteSupplier =
        fun id ->
            received.Add id
            outcome id

    delete, received

[<Fact; Trait("Level", "Unit")>]
let ``deleteSupplier deletes an existing supplier`` () =
    let delete, received = recordingDelete (fun _ -> Ok true)

    let actual = DeleteSupplierWorkflow.deleteSupplier delete "1"

    Assert.Equal(Ok (), actual)
    let attempted = Assert.Single received
    Assert.Equal("1", SupplierId.value attempted)

[<Fact; Trait("Level", "Unit")>]
let ``deleteSupplier rejects an empty identifier and never reaches the store`` () =
    let delete, received = recordingDelete (fun _ -> Ok true)

    let actual = DeleteSupplierWorkflow.deleteSupplier delete "   "

    Assert.Equal(Error (SupplierIdInvalid "Supplier id must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``deleteSupplier reports not found when the store returns false`` () =
    let delete, received = recordingDelete (fun _ -> Ok false)

    let actual = DeleteSupplierWorkflow.deleteSupplier delete "1"

    let expectedId = SupplierId.create "1" |> valueOrFail
    Assert.Equal(Error (SupplierNotFound expectedId), actual)
    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``deleteSupplier returns the store's failure unchanged`` () =
    let delete, _ = recordingDelete (fun _ -> Error (SupplierStoreFailed "database unreachable"))

    let actual = DeleteSupplierWorkflow.deleteSupplier delete "1"

    Assert.Equal(Error (SupplierStoreFailed "database unreachable"), actual)
