module MyDogsbody.Tests.Database.SupplierStoreTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open Dapper.FSharp.SQLite
open MyDogsbody.Builders
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Database.Models

/// No-op logger, so these tests never reach Logging.db.
let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private validSupplier name termDays matchers : ValidSupplier =
    {
        Name = SupplierName.create name |> valueOrFail
        PaymentTermDays = PaymentTermDays.create termDays |> valueOrFail
        Matchers = matchers
    }

let private edit id name termDays matchers : ValidSupplierEdit =
    {
        Id = SupplierId.create id |> valueOrFail
        Name = SupplierName.create name |> valueOrFail
        PaymentTermDays = PaymentTermDays.create termDays |> valueOrFail
        Matchers = matchers
    }

/// Fresh disposable SQLite database per test, schema built by the real migrations - never
/// hand-written DDL, so this doubles as a check that the migrations produce a usable schema.
let private withSuppliers (test: DatabaseContext -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath};Pooling=False"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

    try
        test context
    finally
        context.Dispose()
        try File.Delete databaseFilePath with _ -> ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbody.Exceptions.Types.MyDogsbodyException) ->
        failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

// ---------- integration: round trips against a real SQLite file ----------

[<Fact; Trait("Level", "Integration")>]
let ``getAll returns an empty list for a fresh database`` () =
    withSuppliers (fun context ->
        let actual =
            SupplierStore.getAll handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers ()
            |> okOrFail "getAll"

        Assert.Empty actual
    )

[<Fact; Trait("Level", "Integration")>]
let ``insertOne stores a supplier and getAll returns it with its id surfaced as a string and its matchers attached`` () =
    withSuppliers (fun context ->
        let supplier =
            validSupplier "Acme" 30 [ SupplierMatcher.create Domain "acme.example" |> valueOrFail ]

        let inserted =
            SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers supplier
            |> okOrFail "insertOne"

        Assert.False(String.IsNullOrWhiteSpace(SupplierId.value inserted.Id))
        Assert.Equal("Acme", SupplierName.value inserted.Name)
        Assert.Equal(30, PaymentTermDays.value inserted.PaymentTermDays)
        Assert.Equal<string list>([ "acme.example" ], inserted.Matchers |> List.map SupplierMatcher.value)

        let stored =
            SupplierStore.getAll handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers ()
            |> okOrFail "getAll"

        let readBack = Assert.Single stored
        Assert.Equal(SupplierId.value inserted.Id, SupplierId.value readBack.Id)
        Assert.Equal("Acme", SupplierName.value readBack.Name)
        Assert.Equal<string list>([ "acme.example" ], readBack.Matchers |> List.map SupplierMatcher.value)
    )

[<Fact; Trait("Level", "Integration")>]
let ``insertOne accepts a supplier with no match rules`` () =
    withSuppliers (fun context ->
        let inserted =
            validSupplier "No Rules" 0 []
            |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers
            |> okOrFail "insertOne"

        Assert.Empty inserted.Matchers
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne changes the addressed row and a re-read reflects the replaced matcher set`` () =
    withSuppliers (fun context ->
        let inserted =
            validSupplier "Acme" 30 [ SupplierMatcher.create Domain "old.example" |> valueOrFail ]
            |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers
            |> okOrFail "insertOne"

        let updated =
            edit
                (SupplierId.value inserted.Id)
                "Acme Renamed"
                45
                [ SupplierMatcher.create Sender "new@acme.example" |> valueOrFail ]
            |> SupplierStore.updateOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers
            |> okOrFail "updateOne"

        match updated with
        | Some stored ->
            Assert.Equal("Acme Renamed", SupplierName.value stored.Name)
            Assert.Equal(45, PaymentTermDays.value stored.PaymentTermDays)
            Assert.Equal<string list>([ "new@acme.example" ], stored.Matchers |> List.map SupplierMatcher.value)
        | None -> Assert.Fail("Expected the row to be found")

        let reread =
            SupplierStore.getAll handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers ()
            |> okOrFail "getAll"
            |> List.exactlyOne

        Assert.Equal("Acme Renamed", SupplierName.value reread.Name)
        Assert.Equal<string list>([ "new@acme.example" ], reread.Matchers |> List.map SupplierMatcher.value)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne reports not found for an identifier no row carries`` () =
    withSuppliers (fun context ->
        let updated =
            edit "9999" "Ghost" 0 []
            |> SupplierStore.updateOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers
            |> okOrFail "updateOne"

        Assert.True(Option.isNone updated, "expected None for an unknown identifier")
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleteOne removes the supplier and its matchers, both gone afterwards`` () =
    withSuppliers (fun context ->
        let inserted =
            validSupplier "Acme" 30 [ SupplierMatcher.create Domain "acme.example" |> valueOrFail ]
            |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers
            |> okOrFail "insertOne"

        let deleted =
            SupplierStore.deleteOne handleError context.GetDatabaseConnection context.GetSuppliers inserted.Id
            |> okOrFail "deleteOne"

        Assert.True deleted

        let stored =
            SupplierStore.getAll handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers ()
            |> okOrFail "getAll"

        Assert.Empty stored
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleteOne reports not found for an identifier no row carries`` () =
    withSuppliers (fun context ->
        let deleted =
            SupplierStore.deleteOne handleError context.GetDatabaseConnection context.GetSuppliers (SupplierId.create "9999" |> valueOrFail)
            |> okOrFail "deleteOne"

        Assert.False deleted
    )

[<Fact; Trait("Level", "Integration")>]
let ``the unique index on Name refuses inserting a second supplier with the same name`` () =
    withSuppliers (fun context ->
        validSupplier "Acme" 30 []
        |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers
        |> okOrFail "insertOne first"
        |> ignore

        let secondInsert () =
            validSupplier "Acme" 14 []
            |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers

        match secondInsert () with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected the unique index to refuse a duplicate name")
    )

[<Fact; Trait("Level", "Integration")>]
let ``a supplier name with non-ASCII characters survives the round trip unchanged`` () =
    withSuppliers (fun context ->
        let awkward = "Société Générale"

        validSupplier awkward 0 []
        |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers
        |> okOrFail "insertOne"
        |> ignore

        let stored =
            SupplierStore.getAll handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers ()
            |> okOrFail "getAll"
            |> List.exactlyOne

        Assert.Equal(awkward, SupplierName.value stored.Name)
    )

[<Fact; Trait("Level", "Integration")>]
let ``SupplierApiFactory-shaped round trip: insert a supplier with a zero payment term`` () =
    withSuppliers (fun context ->
        let inserted =
            validSupplier "Due On Issue" 0 []
            |> SupplierStore.insertOne handleError context.GetDatabaseConnection context.GetSuppliers context.GetSupplierMatchers
            |> okOrFail "insertOne"

        Assert.Equal(0, PaymentTermDays.value inserted.PaymentTermDays)
    )

// ---------- unit: error paths, no database reached ----------

let private failingConnection () : SqliteConnection = raise (InvalidOperationException "database is gone")
let private failingSuppliers () : QuerySource<SupplierRecord> = raise (InvalidOperationException "database is gone")
let private failingMatchers () : QuerySource<SupplierMatcherRecord> = raise (InvalidOperationException "database is gone")

let private aValidSupplier = validSupplier "Acme" 30 []
let private aValidEdit = edit "1" "Acme" 30 []

[<Fact; Trait("Level", "Unit")>]
let ``getAll reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual =
        SupplierStore.getAll recordingHandleError failingConnection failingSuppliers failingMatchers ()

    match actual with
    | Error ex ->
        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.SupplierStore.getAll,
            ex.ActionName
        )
        Assert.Equal("Failed to retrieve all suppliers.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``insertOne reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual =
        SupplierStore.insertOne recordingHandleError failingConnection failingSuppliers failingMatchers aValidSupplier

    match actual with
    | Error ex ->
        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.SupplierStore.insertOne,
            ex.ActionName
        )
        Assert.Equal("Failed to insert new supplier.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``updateOne reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual =
        SupplierStore.updateOne recordingHandleError failingConnection failingSuppliers failingMatchers aValidEdit

    match actual with
    | Error ex ->
        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.SupplierStore.updateOne,
            ex.ActionName
        )
        Assert.Equal("Failed to update existing supplier.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``deleteOne reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual =
        SupplierStore.deleteOne recordingHandleError failingConnection failingSuppliers (SupplierId.create "1" |> valueOrFail)

    match actual with
    | Error ex ->
        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.SupplierStore.deleteOne,
            ex.ActionName
        )
        Assert.Equal("Failed to delete existing supplier.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
