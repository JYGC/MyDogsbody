module MyDogsbody.Tests.Contracts.SupplierApiContractTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// SupplierApi is a published interface, so one suite runs against the real API and against the
// in-memory fake the UI's module-creator tests and E2E flow tests use. Without this, the UI
// suite could stay green over a fake that behaves in ways the real API never would.

let private handleError = HandleErrorBuilder (fun _ -> ())

// ---------- the real API, over a temp SQLite file ----------

let private withRealApi (test: SupplierApi -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath}"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

    try
        test (SupplierApiFactory.createSupplierApi handleError context)
    finally
        context.Dispose()
        SqliteConnection.ClearAllPools()
        File.Delete databaseFilePath

// ---------- the in-memory fake ----------

/// A record literal rather than a class, because SupplierApi is a record of functions. Kept
/// faithful to the real API's behaviour - including validation, uniqueness and not-found - which
/// is exactly what this suite is here to enforce.
let private withFakeApi (test: SupplierApi -> unit) =
    let rows = ResizeArray<SupplierUiType>()
    let mutable nextId = 0

    let fail action message = Error (MyDogsbodyException(action, message))

    let validateName (name: string) =
        if String.IsNullOrWhiteSpace name then Some "Supplier name must not be empty."
        elif name.Trim().Length > 200 then Some "Supplier name must be 200 characters or fewer."
        else None

    /// Mirrors PaymentTermDays.create.
    let validatePaymentTerm (days: int) =
        if days < 0 || days > 365 then Some "Payment term days must be between 0 and 365."
        else None

    /// Mirrors SupplierApiMappers.toMatcherKind followed by SupplierMatcher.create - the kind
    /// string is resolved first, then the rule that kind implies.
    let validateMatcher (matcher: SupplierMatcherUiType) =
        let value = if isNull matcher.Value then "" else matcher.Value

        match matcher.Kind with
        | "Sender" when not (value.Contains "@") -> Some "A sender address must contain '@'."
        | "Domain" when value.Contains "@" ->
            Some "A sender domain must not contain '@' - enter a domain, not an address."
        | "Subject" when String.IsNullOrWhiteSpace value -> Some "A subject pattern must not be empty."
        | "Sender" | "Domain" | "Subject" ->
            if value.Length > 400 then Some "Match values must be 400 characters or fewer." else None
        | unknown -> Some $"Matcher kind '{unknown}' has no domain equivalent."

    /// Name, then payment term, then matchers - the order the workflows apply them, so the first
    /// message a caller sees is the same one the real API would have produced.
    let validate (name: string) (paymentTermDays: int) (matchers: SupplierMatcherUiType list) =
        match validateName name with
        | Some reason -> Some reason
        | None ->
            match validatePaymentTerm paymentTermDays with
            | Some reason -> Some reason
            | None -> matchers |> List.tryPick validateMatcher

    /// Returns the name already stored, not the one submitted - the workflows report the existing
    /// row's spelling, which differs whenever the clash is only a difference of case.
    let nameTaken (excludingId: string option) (name: string) =
        rows
        |> Seq.tryFind (fun row ->
            (excludingId |> Option.forall (fun id -> id <> row.Id))
            && String.Equals(row.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun row -> row.Name)

    test
        {
            GetAllSuppliers = fun () -> Ok (List.ofSeq rows)

            AddSupplier =
                fun supplier ->
                    match validate supplier.Name supplier.PaymentTermDays supplier.Matchers with
                    | Some reason -> fail ActionNames.MyDogsbody.Startup.SupplierApi.addSupplier reason
                    | None ->
                        match nameTaken None supplier.Name with
                        | Some storedName ->
                            fail
                                ActionNames.MyDogsbody.Startup.SupplierApi.addSupplier
                                $"The supplier name '{storedName}' is already in use."
                        | None ->
                            nextId <- nextId + 1

                            rows.Add
                                {
                                    Id = string nextId
                                    Name = supplier.Name.Trim()
                                    PaymentTermDays = supplier.PaymentTermDays
                                    Matchers = supplier.Matchers
                                }

                            Ok ()

            EditSupplier =
                fun supplier ->
                    match validate supplier.Name supplier.PaymentTermDays supplier.Matchers with
                    | Some reason -> fail ActionNames.MyDogsbody.Startup.SupplierApi.editSupplier reason
                    | None ->
                        match rows |> Seq.tryFindIndex (fun row -> row.Id = supplier.Id) with
                        | None ->
                            fail
                                ActionNames.MyDogsbody.Startup.SupplierApi.editSupplier
                                $"No supplier was found with id '{supplier.Id}'."
                        | Some index ->
                            match nameTaken (Some supplier.Id) supplier.Name with
                            | Some storedName ->
                                fail
                                    ActionNames.MyDogsbody.Startup.SupplierApi.editSupplier
                                    $"The supplier name '{storedName}' is already in use."
                            | None ->
                                rows.[index] <- { supplier with Name = supplier.Name.Trim() }
                                Ok ()

            DeleteSupplier =
                fun id ->
                    match rows |> Seq.tryFindIndex (fun row -> row.Id = id) with
                    | None ->
                        fail ActionNames.MyDogsbody.Startup.SupplierApi.deleteSupplier $"No supplier was found with id '{id}'."
                    | Some index ->
                        rows.RemoveAt index
                        Ok ()
        }

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq =
    [
        [| box "real api" |]
        [| box "fake api" |]
    ]

let private withImplementation (name: string) (test: SupplierApi -> unit) =
    match name with
    | "real api" -> withRealApi test
    | "fake api" -> withFakeApi test
    | other -> failwith $"Unknown implementation '{other}'"

let private aSupplier: SupplierUiTypeWithoutId =
    { Name = "Acme"; PaymentTermDays = 30; Matchers = [ { Kind = "Domain"; Value = "acme.example" } ] }

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

let private errorOrFail label result =
    match result with
    | Error (ex: MyDogsbodyException) -> ex
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

// ---------- the shared suite ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``GetAllSuppliers returns an empty list before anything is added`` (implementation: string) =
    withImplementation implementation (fun api ->
        Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddSupplier stores every field and assigns an identifier`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"

        let stored = Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
        Assert.False(String.IsNullOrWhiteSpace stored.Id)
        Assert.Equal("Acme", stored.Name)
        Assert.Equal(30, stored.PaymentTermDays)
        Assert.Equal<SupplierMatcherUiType list>([ { Kind = "Domain"; Value = "acme.example" } ], stored.Matchers)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``EditSupplier changes the addressed supplier`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"
        let stored = Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")

        api.EditSupplier
            { Id = stored.Id; Name = "Acme Renamed"; PaymentTermDays = 45; Matchers = [] }
        |> okOrFail "EditSupplier"

        let reloaded = Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
        Assert.Equal(stored.Id, reloaded.Id)
        Assert.Equal("Acme Renamed", reloaded.Name)
        Assert.Equal(45, reloaded.PaymentTermDays)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DeleteSupplier removes the addressed supplier`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"
        let stored = Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")

        api.DeleteSupplier stored.Id |> okOrFail "DeleteSupplier"

        Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddSupplier rejects an empty name with the documented message`` (implementation: string) =
    withImplementation implementation (fun api ->
        let actual = api.AddSupplier { aSupplier with Name = "   " } |> errorOrFail "AddSupplier"

        Assert.Equal("Supplier name must not be empty.", actual.Message)
        Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddSupplier rejects a name that is already taken`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"

        let actual = api.AddSupplier { aSupplier with PaymentTermDays = 0 } |> errorOrFail "AddSupplier"

        Assert.Equal("The supplier name 'Acme' is already in use.", actual.Message)
        Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers") |> ignore
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``EditSupplier reports not found for an unknown identifier`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"

        let actual =
            api.EditSupplier { Id = "9999"; Name = "Ghost"; PaymentTermDays = 0; Matchers = [] }
            |> errorOrFail "EditSupplier"

        Assert.Equal("No supplier was found with id '9999'.", actual.Message)
        Assert.Equal("Acme", (Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")).Name)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``EditSupplier rejects an empty name and changes nothing`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"
        let stored = Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")

        let actual = api.EditSupplier { stored with Name = "" } |> errorOrFail "EditSupplier"

        Assert.Equal("Supplier name must not be empty.", actual.Message)
        Assert.Equal("Acme", (Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")).Name)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DeleteSupplier reports not found for an unknown identifier`` (implementation: string) =
    withImplementation implementation (fun api ->
        let actual = api.DeleteSupplier "9999" |> errorOrFail "DeleteSupplier"

        Assert.Equal("No supplier was found with id '9999'.", actual.Message)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddSupplier rejects a payment term above the maximum`` (implementation: string) =
    withImplementation implementation (fun api ->
        let actual =
            api.AddSupplier { aSupplier with PaymentTermDays = 366 } |> errorOrFail "AddSupplier"

        Assert.Equal("Payment term days must be between 0 and 365.", actual.Message)
        Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddSupplier rejects a sender matcher with no at sign`` (implementation: string) =
    withImplementation implementation (fun api ->
        let actual =
            api.AddSupplier { aSupplier with Matchers = [ { Kind = "Sender"; Value = "acme.example" } ] }
            |> errorOrFail "AddSupplier"

        Assert.Equal("A sender address must contain '@'.", actual.Message)
        Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``a name taken report names the spelling already stored, not the one submitted`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.AddSupplier aSupplier |> okOrFail "AddSupplier"

        let actual = api.AddSupplier { aSupplier with Name = "ACME" } |> errorOrFail "AddSupplier"

        Assert.Equal("The supplier name 'Acme' is already in use.", actual.Message)
        Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers") |> ignore
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddSupplier reports an unrecognised matcher kind as an Error rather than raising`` (implementation: string) =
    withImplementation implementation (fun api ->
        let actual =
            api.AddSupplier { aSupplier with Matchers = [ { Kind = "Nonsense"; Value = "acme.example" } ] }
            |> errorOrFail "AddSupplier"

        Assert.False(String.IsNullOrWhiteSpace actual.Message)
        Assert.Empty(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an awkward name survives the api round trip`` (implementation: string) =
    withImplementation implementation (fun api ->
        let awkward = "Société Générale"

        api.AddSupplier { aSupplier with Name = awkward } |> okOrFail "AddSupplier"

        Assert.Equal(awkward, (Assert.Single(api.GetAllSuppliers() |> okOrFail "GetAllSuppliers")).Name)
    )
