module MyDogsbody.Tests.Contracts.SupplierPersistedShapeTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Database
open MyDogsbody.Database.Migrations

// SQLite is not schemaless the way LiteDB is, but a Dapper.FSharp record field renamed without a
// migration fails at run time only - so the persisted names are asserted here by reading the
// table schema, not just by round-tripping an object.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private withMigratedDatabase (test: DatabaseContext -> string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Pooling=False"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

    try
        test context connectionString
    finally
        context.Dispose()
        try File.Delete databaseFilePath with _ -> ()

let private columnNames (connectionString: string) (tableName: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{tableName}')"
    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            yield reader.GetString 1
    ]

[<Fact; Trait("Level", "Contract")>]
let ``a supplier is persisted under the documented column names`` () =
    withMigratedDatabase (fun context connectionString ->
        Assert.Equal<string list>(
            [ "Id"; "Name"; "PaymentTermDays" ],
            columnNames connectionString "Suppliers"
        )
    )

[<Fact; Trait("Level", "Contract")>]
let ``a matcher is persisted under the documented column names`` () =
    withMigratedDatabase (fun context connectionString ->
        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "Kind"; "Value" ],
            columnNames connectionString "SupplierMatchers"
        )
    )

[<Fact; Trait("Level", "Contract")>]
let ``a supplier row carries no severity-like or UI-named column`` () =
    // The UI calls the identifier Id too, but the field the UI renames - none here, suppliers
    // carry no renamed field the way credentials rename Username - still worth pinning so a
    // future rename is caught the moment it happens.
    withMigratedDatabase (fun context connectionString ->
        let columns = columnNames connectionString "Suppliers"
        Assert.DoesNotContain("Severity", columns)
        Assert.DoesNotContain("Level", columns)
    )

[<Fact; Trait("Level", "Contract")>]
let ``a SupplierName survives the round trip through its TEXT column unchanged, including non-ASCII`` () =
    withMigratedDatabase (fun context _ ->
        let awkward = "Société Générale Ltd. — Facturation"

        let inserted =
            {
                Name = SupplierName.create awkward |> valueOrFail
                PaymentTermDays = PaymentTermDays.create 0 |> valueOrFail
                Matchers = []
            }
            |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers

        match inserted with
        | Ok stored -> Assert.Equal(awkward, SupplierName.value stored.Name)
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

[<Fact; Trait("Level", "Contract")>]
let ``a SupplierName at the maximum length survives the round trip unchanged`` () =
    withMigratedDatabase (fun context _ ->
        let atLimit = String('a', SupplierName.MaximumLength)

        let inserted =
            {
                Name = SupplierName.create atLimit |> valueOrFail
                PaymentTermDays = PaymentTermDays.create 0 |> valueOrFail
                Matchers = []
            }
            |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers

        match inserted with
        | Ok stored ->
            Assert.Equal(atLimit, SupplierName.value stored.Name)
            Assert.Equal(SupplierName.MaximumLength, (SupplierName.value stored.Name).Length)
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

[<Fact; Trait("Level", "Contract")>]
let ``every MatcherKind value survives the round trip through its TEXT column unchanged`` () =
    withMigratedDatabase (fun context _ ->
        let allCases =
            Reflection.FSharpType.GetUnionCases(typeof<MatcherKind>)
            |> Array.map (fun case -> Reflection.FSharpValue.MakeUnion(case, [||]) :?> MatcherKind)

        let matchers =
            [
                SupplierMatcher.create Sender "billing@acme.example" |> valueOrFail
                SupplierMatcher.create Domain "acme.example" |> valueOrFail
                SupplierMatcher.create Subject "your invoice" |> valueOrFail
            ]

        let inserted =
            {
                Name = SupplierName.create "Acme" |> valueOrFail
                PaymentTermDays = PaymentTermDays.create 0 |> valueOrFail
                Matchers = matchers
            }
            |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers

        match inserted with
        | Ok stored ->
            Assert.Equal(allCases.Length, List.length stored.Matchers)

            for kind in allCases do
                Assert.Contains(stored.Matchers, fun m -> SupplierMatcher.kind m = kind)
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )
