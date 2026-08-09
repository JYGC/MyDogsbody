module MyDogsbody.Tests.Domain.Suppliers.EditSupplierWorkflowTests

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

let private acmeRow = storedSupplier "1" "Acme" 30 []
let private bravoRow = storedSupplier "2" "Bravo Corp" 14 []

/// Records every update attempt, so "the store was never reached" is assertable.
let private recordingUpdate (outcome: ValidSupplierEdit -> Result<StoredSupplier option, SupplierError>) =
    let received = ResizeArray<ValidSupplierEdit>()

    let update: UpdateSupplier =
        fun edit ->
            received.Add edit
            outcome edit

    update, received

let private updateSucceeds: ValidSupplierEdit -> Result<StoredSupplier option, SupplierError> =
    fun edit ->
        Ok (
            Some
                {
                    Id = edit.Id
                    Name = edit.Name
                    PaymentTermDays = edit.PaymentTermDays
                    Matchers = edit.Matchers
                }
        )

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier updates an existing supplier and returns every stored field`` () =
    let load: LoadSuppliers = fun () -> Ok [ acmeRow; bravoRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedSupplierEdit =
        {
            Id = "1"
            Name = "Acme Renamed"
            PaymentTermDays = 45
            Matchers = [ Domain, "acme.example" ]
        }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    match actual with
    | Ok stored ->
        Assert.Equal("1", SupplierId.value stored.Id)
        Assert.Equal("Acme Renamed", SupplierName.value stored.Name)
        Assert.Equal(45, PaymentTermDays.value stored.PaymentTermDays)
        Assert.Equal<string list>([ "acme.example" ], stored.Matchers |> List.map SupplierMatcher.value)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let attempted = Assert.Single received
    Assert.Equal("1", SupplierId.value attempted.Id)
    Assert.Equal("Acme Renamed", SupplierName.value attempted.Name)

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier replaces the match rule set rather than merging it`` () =
    let existingMatcher = SupplierMatcher.create Domain "old.example" |> valueOrFail
    let existingRow = storedSupplier "1" "Acme" 30 [ existingMatcher ]
    let load: LoadSuppliers = fun () -> Ok [ existingRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedSupplierEdit =
        {
            Id = "1"
            Name = "Acme"
            PaymentTermDays = 30
            Matchers = [ Sender, "new@acme.example" ]
        }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    let attempted = Assert.Single received
    Assert.Equal<string list>([ "new@acme.example" ], attempted.Matchers |> List.map SupplierMatcher.value)

    match actual with
    | Ok stored -> Assert.Equal<string list>([ "new@acme.example" ], stored.Matchers |> List.map SupplierMatcher.value)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier reports not found when no stored supplier has that identifier`` () =
    let load: LoadSuppliers = fun () -> Ok [ acmeRow; bravoRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedSupplierEdit =
        { Id = "99"; Name = "Ghost"; PaymentTermDays = 0; Matchers = [] }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    let expectedId = SupplierId.create "99" |> valueOrFail
    Assert.Equal(Error (SupplierNotFound expectedId), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier reports name taken when renaming onto another supplier's name`` () =
    let load: LoadSuppliers = fun () -> Ok [ acmeRow; bravoRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedSupplierEdit =
        { Id = "1"; Name = "Bravo Corp"; PaymentTermDays = 30; Matchers = [] }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    Assert.Equal(Error (SupplierNameTaken "Bravo Corp"), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier does not report a name clash against the row's own unchanged name`` () =
    let load: LoadSuppliers = fun () -> Ok [ acmeRow; bravoRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedSupplierEdit =
        { Id = "1"; Name = "Acme"; PaymentTermDays = 60; Matchers = [] }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    match actual with
    | Ok stored -> Assert.Equal(60, PaymentTermDays.value stored.PaymentTermDays)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier reports not found when the store loses the row between load and update`` () =
    let load: LoadSuppliers = fun () -> Ok [ acmeRow ]
    let update, received = recordingUpdate (fun _ -> Ok None)

    let submitted: UnvalidatedSupplierEdit =
        { Id = "1"; Name = "Acme"; PaymentTermDays = 30; Matchers = [] }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    let expectedId = SupplierId.create "1" |> valueOrFail
    Assert.Equal(Error (SupplierNotFound expectedId), actual)
    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier rejects an empty identifier and never loads or updates`` () =
    let mutable loaded = false
    let load: LoadSuppliers = fun () -> loaded <- true; Ok [ acmeRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedSupplierEdit =
        { Id = "  "; Name = "Acme"; PaymentTermDays = 30; Matchers = [] }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    Assert.Equal(Error (SupplierIdInvalid "Supplier id must not be empty."), actual)
    Assert.False(loaded, "validation failed, so the store must not have been read")
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier rejects an invalid match rule and never updates`` () =
    let load: LoadSuppliers = fun () -> Ok [ acmeRow ]
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedSupplierEdit =
        {
            Id = "1"
            Name = "Acme"
            PaymentTermDays = 30
            Matchers = [ Subject, "" ]
        }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    Assert.Equal(Error (MatcherInvalid "A subject pattern must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier returns a load failure unchanged and never updates`` () =
    let load: LoadSuppliers = fun () -> Error (SupplierStoreFailed "database locked")
    let update, received = recordingUpdate updateSucceeds

    let submitted: UnvalidatedSupplierEdit =
        { Id = "1"; Name = "Acme"; PaymentTermDays = 30; Matchers = [] }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    Assert.Equal(Error (SupplierStoreFailed "database locked"), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editSupplier returns an update failure unchanged`` () =
    let load: LoadSuppliers = fun () -> Ok [ acmeRow ]
    let update, _ = recordingUpdate (fun _ -> Error (SupplierStoreFailed "write failed"))

    let submitted: UnvalidatedSupplierEdit =
        { Id = "1"; Name = "Acme"; PaymentTermDays = 30; Matchers = [] }

    let actual = EditSupplierWorkflow.editSupplier load update submitted

    Assert.Equal(Error (SupplierStoreFailed "write failed"), actual)
