module MyDogsbody.Tests.Contracts.SupplierDependencyContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup

// A dependency function type is this architecture's published interface, so CLAUDE.md's shared-
// suite rule applies to each one: the suite below runs against the real adapter binding AND
// against every fake the workflow unit tests stand in for it.
//
// The failure this catches: a fake returning a shape the real store never produces, leaving a
// workflow's unit suite green over code that cannot work in production.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private aSupplier name termDays matchers : ValidSupplier =
    {
        Name = SupplierName.create name |> valueOrFail
        PaymentTermDays = PaymentTermDays.create termDays |> valueOrFail
        Matchers = matchers
    }

let private anEdit id name termDays matchers : ValidSupplierEdit =
    {
        Id = SupplierId.create id |> valueOrFail
        Name = SupplierName.create name |> valueOrFail
        PaymentTermDays = PaymentTermDays.create termDays |> valueOrFail
        Matchers = matchers
    }

/// The four dependencies, bound together over one store, however that store is implemented.
type private SupplierDependencies =
    {
        Load: LoadSuppliers
        Save: SaveSupplier
        Update: UpdateSupplier
        Delete: DeleteSupplier
    }

// ---------- the real adapter, over a temp SQLite file, schema built by the real migrations ----------

let private withRealDependencies (test: SupplierDependencies -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Pooling=False"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

    try
        test
            {
                Load =
                    fun () ->
                        SupplierStore.getAll
                            handleError
                            context.GetDatabaseConnection
                            context.GetSuppliers
                            context.GetSupplierMatchers
                            ()
                        |> Result.mapError SupplierApiMappers.toSupplierError
                Save =
                    fun supplier ->
                        SupplierStore.insertOne
                            handleError
                            context.GetDatabaseConnection
                            context.GetSuppliers
                            context.GetSupplierMatchers
                            supplier
                        |> Result.mapError SupplierApiMappers.toSupplierError
                Update =
                    fun edit ->
                        SupplierStore.updateOne
                            handleError
                            context.GetDatabaseConnection
                            context.GetSuppliers
                            context.GetSupplierMatchers
                            edit
                        |> Result.mapError SupplierApiMappers.toSupplierError
                Delete =
                    fun id ->
                        SupplierStore.deleteOne handleError context.GetDatabaseConnection context.GetSuppliers id
                        |> Result.mapError SupplierApiMappers.toSupplierError
            }
    finally
        context.Dispose()
        try File.Delete databaseFilePath with _ -> ()

// ---------- the in-memory fake the workflow unit tests use ----------

let private withFakeDependencies (test: SupplierDependencies -> unit) =
    let rows = ResizeArray<StoredSupplier>()
    let mutable nextId = 0

    let newId () =
        nextId <- nextId + 1
        SupplierId.create (string nextId) |> valueOrFail

    test
        {
            Load = fun () -> Ok (List.ofSeq rows)

            Save =
                fun supplier ->
                    let stored =
                        {
                            Id = newId ()
                            Name = supplier.Name
                            PaymentTermDays = supplier.PaymentTermDays
                            Matchers = supplier.Matchers
                        }

                    rows.Add stored
                    Ok stored

            Update =
                fun edit ->
                    match rows |> Seq.tryFindIndex (fun row -> row.Id = edit.Id) with
                    | None -> Ok None
                    | Some index ->
                        let updated =
                            {
                                Id = edit.Id
                                Name = edit.Name
                                PaymentTermDays = edit.PaymentTermDays
                                Matchers = edit.Matchers
                            }

                        rows.[index] <- updated
                        Ok (Some updated)

            Delete =
                fun id ->
                    match rows |> Seq.tryFindIndex (fun row -> row.Id = id) with
                    | None -> Ok false
                    | Some index ->
                        rows.RemoveAt index
                        Ok true
        }

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq =
    [
        [| box "real adapter" |]
        [| box "in-memory fake" |]
    ]

let private withImplementation (name: string) (test: SupplierDependencies -> unit) =
    match name with
    | "real adapter" -> withRealDependencies test
    | "in-memory fake" -> withFakeDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (error: SupplierError) -> failwith $"{label} expected Ok, but got Error: {error}"

// ---------- the shared suite ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``LoadSuppliers returns an empty list for an empty store`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        Assert.Empty(dependencies.Load() |> okOrFail "Load")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SaveSupplier returns the supplier with a non-empty identifier`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let actual =
            aSupplier "Acme" 30 [ SupplierMatcher.create Domain "acme.example" |> valueOrFail ]
            |> dependencies.Save
            |> okOrFail "Save"

        Assert.False(String.IsNullOrWhiteSpace(SupplierId.value actual.Id))
        Assert.Equal("Acme", SupplierName.value actual.Name)
        Assert.Equal(30, PaymentTermDays.value actual.PaymentTermDays)
        Assert.Equal<string list>([ "acme.example" ], actual.Matchers |> List.map SupplierMatcher.value)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``a saved supplier is visible to LoadSuppliers`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let saved = aSupplier "Bravo" 14 [] |> dependencies.Save |> okOrFail "Save"

        let loaded = dependencies.Load() |> okOrFail "Load"

        let readBack = Assert.Single loaded
        Assert.Equal(SupplierId.value saved.Id, SupplierId.value readBack.Id)
        Assert.Equal("Bravo", SupplierName.value readBack.Name)
        Assert.Equal(14, PaymentTermDays.value readBack.PaymentTermDays)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SaveSupplier gives each supplier a distinct identifier`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let first = aSupplier "First" 0 [] |> dependencies.Save |> okOrFail "Save"
        let second = aSupplier "Second" 0 [] |> dependencies.Save |> okOrFail "Save"

        Assert.NotEqual<string>(SupplierId.value first.Id, SupplierId.value second.Id)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateSupplier returns the updated supplier when the identifier matches`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let saved = aSupplier "Acme" 30 [] |> dependencies.Save |> okOrFail "Save"

        let actual =
            anEdit (SupplierId.value saved.Id) "Acme Renamed" 45 []
            |> dependencies.Update
            |> okOrFail "Update"

        match actual with
        | Some updated ->
            Assert.Equal(SupplierId.value saved.Id, SupplierId.value updated.Id)
            Assert.Equal("Acme Renamed", SupplierName.value updated.Name)
            Assert.Equal(45, PaymentTermDays.value updated.PaymentTermDays)
        | None -> Assert.Fail("Expected the row to be found")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateSupplier returns None when the identifier matches nothing`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        aSupplier "Acme" 30 [] |> dependencies.Save |> okOrFail "Save" |> ignore

        let actual = anEdit "9999" "Ghost" 0 [] |> dependencies.Update |> okOrFail "Update"

        Assert.True(Option.isNone actual)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateSupplier replaces the matcher set rather than merging it`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let saved =
            aSupplier "Acme" 30 [ SupplierMatcher.create Domain "old.example" |> valueOrFail ]
            |> dependencies.Save
            |> okOrFail "Save"

        let updated =
            anEdit
                (SupplierId.value saved.Id)
                "Acme"
                30
                [ SupplierMatcher.create Sender "new@acme.example" |> valueOrFail ]
            |> dependencies.Update
            |> okOrFail "Update"

        match updated with
        | Some stored ->
            Assert.Equal<string list>([ "new@acme.example" ], stored.Matchers |> List.map SupplierMatcher.value)
        | None -> Assert.Fail("Expected the row to be found")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an update is visible to a later load`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let saved = aSupplier "Acme" 30 [] |> dependencies.Save |> okOrFail "Save"

        anEdit (SupplierId.value saved.Id) "Renamed" 60 [] |> dependencies.Update |> okOrFail "Update" |> ignore

        let loaded = dependencies.Load() |> okOrFail "Load"
        let reloaded = Assert.Single loaded
        Assert.Equal("Renamed", SupplierName.value reloaded.Name)
        Assert.Equal(60, PaymentTermDays.value reloaded.PaymentTermDays)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DeleteSupplier returns true and removes the row when the identifier matches`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let saved = aSupplier "Acme" 30 [] |> dependencies.Save |> okOrFail "Save"

        let deleted = dependencies.Delete saved.Id |> okOrFail "Delete"

        Assert.True deleted
        Assert.Empty(dependencies.Load() |> okOrFail "Load")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DeleteSupplier returns false when the identifier matches nothing`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let deleted =
            dependencies.Delete (SupplierId.create "9999" |> valueOrFail) |> okOrFail "Delete"

        Assert.False deleted
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an awkward supplier name survives a save and load unchanged`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let awkward = "Société Générale"

        aSupplier awkward 0 [] |> dependencies.Save |> okOrFail "Save" |> ignore
        let loaded = dependencies.Load() |> okOrFail "Load"

        Assert.Equal(awkward, SupplierName.value (Assert.Single loaded).Name)
    )
