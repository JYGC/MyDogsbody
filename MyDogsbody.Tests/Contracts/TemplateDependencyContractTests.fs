module MyDogsbody.Tests.Contracts.TemplateDependencyContractTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup

// A dependency function type is this architecture's published interface, so CLAUDE.md's shared-
// suite rule applies to each one: the suite below runs against the real adapter binding AND
// against an in-memory fake, so a fake returning a shape the real store never produces cannot
// leave a workflow's unit suite green over code that cannot work in production.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private validRules =
    [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
      { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
      { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]

let private aValidTemplate supplierId : ValidTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = supplierId; Name = "Monthly statement"; Part = AnyPart; Position = 0; Rules = validRules }

    match ValidateTemplateWorkflow.validateTemplate unvalidated with
    | Ok template -> template
    | Error error -> failwith $"Test setup produced an invalid template: {error}"

/// The six dependencies, bound together over one store, however that store is implemented.
type private TemplateDependencies =
    {
        LoadForSupplier: LoadTemplatesForSupplier
        Save: SaveTemplate
        Update: UpdateTemplate
        Delete: DeleteTemplate
        Reorder: ReorderTemplates
        LoadSuppliers: LoadSuppliersForTemplates
    }

// ---------- the real adapter, over a temp SQLite file, schema built by the real migrations ----------

let private withRealDependencies (test: TemplateDependencies -> string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath}"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath

    let supplierIdString =
        let connection = context.GetDatabaseConnection()
        connection.Open()
        try
            use command = connection.CreateCommand()
            command.CommandText <- "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30); SELECT last_insert_rowid();"
            string (Convert.ToInt64(command.ExecuteScalar()))
        finally
            connection.Close()

    try
        test
            {
                LoadForSupplier =
                    fun supplierId ->
                        TemplateStore.getForSupplier
                            handleError
                            context.GetDatabaseConnection
                            context.GetInvoiceTemplates
                            context.GetTemplateFieldRules
                            supplierId
                        |> Result.mapError TemplateApiMappers.toTemplateError
                Save =
                    fun template ->
                        TemplateStore.insertOne
                            handleError
                            context.GetDatabaseConnection
                            context.GetInvoiceTemplates
                            context.GetTemplateFieldRules
                            template
                        |> Result.mapError TemplateApiMappers.toTemplateError
                Update =
                    fun templateId template ->
                        TemplateStore.updateOne
                            handleError
                            context.GetDatabaseConnection
                            context.GetInvoiceTemplates
                            context.GetTemplateFieldRules
                            templateId
                            template
                        |> Result.mapError TemplateApiMappers.toTemplateError
                Delete =
                    fun id ->
                        TemplateStore.deleteOne handleError context.GetDatabaseConnection context.GetInvoiceTemplates id
                        |> Result.mapError TemplateApiMappers.toTemplateError
                Reorder =
                    fun supplierId templateIds ->
                        TemplateStore.reorder handleError context.GetDatabaseConnection context.GetInvoiceTemplates supplierId templateIds
                        |> Result.mapError TemplateApiMappers.toTemplateError
                LoadSuppliers =
                    fun () ->
                        SupplierStore.getAll
                            handleError
                            context.GetDatabaseConnection
                            context.GetSuppliers
                            context.GetSupplierMatchers
                            ()
                        |> Result.mapError (fun ex -> TemplateStoreFailed ex.Message)
            }
            supplierIdString
    finally
        context.Dispose()
        SqliteConnection.ClearAllPools()
        File.Delete databaseFilePath

// ---------- the in-memory fake the workflow unit tests use ----------

let private withFakeDependencies (test: TemplateDependencies -> string -> unit) =
    let rows = ResizeArray<StoredTemplate>()
    let mutable nextId = 0
    let supplierIdString = "1"
    let supplier: StoredSupplier =
        {
            Id = SupplierId.create supplierIdString |> valueOrFail
            Name = SupplierName.create "Acme" |> valueOrFail
            PaymentTermDays = PaymentTermDays.create 30 |> valueOrFail
            Matchers = []
        }

    let newId () =
        nextId <- nextId + 1
        TemplateId.create (string nextId) |> valueOrFail

    test
        {
            LoadForSupplier =
                fun supplierId ->
                    rows
                    |> Seq.filter (fun row -> ValidTemplate.supplierId row.Template = supplierId)
                    |> List.ofSeq
                    |> Ok

            Save =
                fun template ->
                    let stored = { Id = newId (); Template = template }
                    rows.Add stored
                    Ok stored

            Update =
                fun templateId template ->
                    match rows |> Seq.tryFindIndex (fun row -> row.Id = templateId) with
                    | None -> Ok None
                    | Some index ->
                        let updated = { Id = templateId; Template = template }
                        rows.[index] <- updated
                        Ok (Some updated)

            Delete =
                fun id ->
                    match rows |> Seq.tryFindIndex (fun row -> row.Id = id) with
                    | None -> Ok false
                    | Some index ->
                        rows.RemoveAt index
                        Ok true

            Reorder =
                fun _ templateIds ->
                    templateIds
                    |> List.iteri (fun index templateId ->
                        match rows |> Seq.tryFindIndex (fun row -> row.Id = templateId) with
                        | None -> ()
                        | Some rowIndex ->
                            let existing = rows.[rowIndex]
                            // Reconstructs rather than validating again - position is plain data,
                            // not something ValidateTemplateWorkflow checks.
                            match
                                ValidateTemplateWorkflow.reconstructValidTemplate
                                    (ValidTemplate.supplierId existing.Template)
                                    (ValidTemplate.name existing.Template)
                                    (ValidTemplate.part existing.Template)
                                    index
                                    (ValidTemplate.rules existing.Template)
                            with
                            | Ok repositioned -> rows.[rowIndex] <- { existing with Template = repositioned }
                            | Error _ -> ())
                    Ok ()

            LoadSuppliers = fun () -> Ok [ supplier ]
        }
        supplierIdString

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq =
    [
        [| box "real adapter" |]
        [| box "in-memory fake" |]
    ]

let private withImplementation (name: string) (test: TemplateDependencies -> string -> unit) =
    match name with
    | "real adapter" -> withRealDependencies test
    | "in-memory fake" -> withFakeDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (error: TemplateError) -> failwith $"{label} expected Ok, but got Error: {error}"

// ---------- the shared suite ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``LoadTemplatesForSupplier returns an empty list for a supplier with no templates`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let supplierId = SupplierId.create supplierIdString |> valueOrFail
        Assert.Empty(dependencies.LoadForSupplier supplierId |> okOrFail "LoadForSupplier")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SaveTemplate returns the template with a non-empty identifier`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let actual = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"

        Assert.False(String.IsNullOrWhiteSpace(TemplateId.value actual.Id))
        Assert.Equal("Monthly statement", TemplateName.value (ValidTemplate.name actual.Template))
        Assert.Equal<TargetField list>([ Reference; Amount; Currency ], ValidTemplate.rules actual.Template |> List.map (fun r -> r.Field))
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``a saved template is visible to LoadTemplatesForSupplier`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let saved = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"
        let supplierId = SupplierId.create supplierIdString |> valueOrFail

        let loaded = dependencies.LoadForSupplier supplierId |> okOrFail "LoadForSupplier"

        let readBack = Assert.Single loaded
        Assert.Equal(TemplateId.value saved.Id, TemplateId.value readBack.Id)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SaveTemplate gives each template a distinct identifier`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let first = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"
        let second = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"

        Assert.NotEqual<string>(TemplateId.value first.Id, TemplateId.value second.Id)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateTemplate returns the updated template when the identifier matches`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let saved = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"

        let renamed: ValidTemplate =
            let unvalidated: UnvalidatedTemplate =
                { SupplierId = supplierIdString; Name = "Renamed"; Part = AnyPart; Position = 0; Rules = validRules }
            ValidateTemplateWorkflow.validateTemplate unvalidated
            |> function Ok t -> t | Error e -> failwith $"{e}"

        let actual = dependencies.Update saved.Id renamed |> okOrFail "Update"

        match actual with
        | Some updated ->
            Assert.Equal(TemplateId.value saved.Id, TemplateId.value updated.Id)
            Assert.Equal("Renamed", TemplateName.value (ValidTemplate.name updated.Template))
        | None -> Assert.Fail("Expected the row to be found")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateTemplate returns None when the identifier matches nothing`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save" |> ignore
        let ghostId = TemplateId.create "9999" |> valueOrFail

        let actual = dependencies.Update ghostId (aValidTemplate supplierIdString) |> okOrFail "Update"

        Assert.True(Option.isNone actual)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateTemplate replaces the rule set rather than merging it`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let saved = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"

        let smallerRules: ValidTemplate =
            let unvalidated: UnvalidatedTemplate =
                {
                    SupplierId = supplierIdString
                    Name = "Monthly statement"
                    Part = AnyPart
                    Position = 0
                    Rules =
                        [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }
                          { Field = Amount; Rule = AfterLabel "Owed:"; Hint = AsMoney '.' }
                          { Field = Currency; Rule = FixedValue "USD"; Hint = AsText } ]
                }
            ValidateTemplateWorkflow.validateTemplate unvalidated
            |> function Ok t -> t | Error e -> failwith $"{e}"

        let updated = dependencies.Update saved.Id smallerRules |> okOrFail "Update"

        match updated with
        | Some stored -> Assert.Equal(FixedValue "USD", (ValidTemplate.rules stored.Template |> List.find (fun r -> r.Field = Currency)).Rule)
        | None -> Assert.Fail("Expected the row to be found")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DeleteTemplate returns true and removes the row when the identifier matches`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let saved = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"

        let deleted = dependencies.Delete saved.Id |> okOrFail "Delete"

        Assert.True deleted
        Assert.Empty(dependencies.LoadForSupplier (SupplierId.create supplierIdString |> valueOrFail) |> okOrFail "LoadForSupplier")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DeleteTemplate returns false when the identifier matches nothing`` (implementation: string) =
    withImplementation implementation (fun dependencies _ ->
        let deleted = dependencies.Delete (TemplateId.create "9999" |> valueOrFail) |> okOrFail "Delete"

        Assert.False deleted
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``Reorder persists the new order`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let first = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"
        let second = aValidTemplate supplierIdString |> dependencies.Save |> okOrFail "Save"
        let supplierId = SupplierId.create supplierIdString |> valueOrFail

        dependencies.Reorder supplierId [ second.Id; first.Id ] |> okOrFail "Reorder"

        let reread =
            dependencies.LoadForSupplier supplierId
            |> okOrFail "LoadForSupplier"
            |> List.sortBy (fun t -> ValidTemplate.position t.Template)

        Assert.Equal<string list>(
            [ TemplateId.value second.Id; TemplateId.value first.Id ],
            reread |> List.map (fun t -> TemplateId.value t.Id)
        )
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``LoadSuppliersForTemplates returns the stored supplier`` (implementation: string) =
    withImplementation implementation (fun dependencies supplierIdString ->
        let suppliers = dependencies.LoadSuppliers() |> okOrFail "LoadSuppliers"

        Assert.Contains(suppliers, fun s -> SupplierId.value s.Id = supplierIdString)
    )
