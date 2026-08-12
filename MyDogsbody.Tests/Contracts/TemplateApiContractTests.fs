module MyDogsbody.Tests.Contracts.TemplateApiContractTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// TemplateApi is a published interface, so one suite runs against the real API and against an
// in-memory fake. Unlike SupplierApiContractTests's fake, which hand-rolls its own copy of every
// validation rule, this fake reuses ValidateTemplateWorkflow.validateTemplate and the real CRUD
// workflows (AddTemplateWorkflow, EditTemplateWorkflow, ...) bound over in-memory dependencies -
// deliberately, not out of laziness: Templates' validation surface is a dozen-plus rules (offset
// range, capture groups, date formats, DateFromField soundness, duplicate/required fields), and
// hand-duplicating all of them here would risk the fake itself drifting from the real rules,
// which is exactly the kind of bug this suite exists to catch. Those rules are already exhaustively
// unit-tested case by case in ValidateTemplateWorkflowTests.fs (CLAUDE.md's Unit level). What this
// suite is uniquely positioned to catch instead - and does - is drift in the API/workflow
// composition and in storage-substituted CRUD behaviour: not-found handling, replace-not-merge on
// edit, and reorder completeness enforcement, all exercised identically against a real SQLite-backed
// adapter and an in-memory one.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

// ---------- the real API, over a temp SQLite file ----------

let private withRealApi (test: TemplateApi -> string -> unit) =
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
        test (TemplateApiFactory.createTemplateApi handleError context) supplierIdString
    finally
        context.Dispose()
        SqliteConnection.ClearAllPools()
        File.Delete databaseFilePath

// ---------- the in-memory fake ----------

let private withFakeApi (test: TemplateApi -> string -> unit) =
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

    let loadTemplatesForSupplier: LoadTemplatesForSupplier =
        fun supplierId ->
            rows
            |> Seq.filter (fun row -> ValidTemplate.supplierId row.Template = supplierId)
            |> List.ofSeq
            |> Ok

    let saveTemplate: SaveTemplate =
        fun template ->
            let stored = { Id = newId (); Template = template }
            rows.Add stored
            Ok stored

    let updateTemplate: UpdateTemplate =
        fun templateId template ->
            match rows |> Seq.tryFindIndex (fun row -> row.Id = templateId) with
            | None -> Ok None
            | Some index ->
                let updated = { Id = templateId; Template = template }
                rows.[index] <- updated
                Ok (Some updated)

    let deleteTemplateDependency: DeleteTemplate =
        fun id ->
            match rows |> Seq.tryFindIndex (fun row -> row.Id = id) with
            | None -> Ok false
            | Some index ->
                rows.RemoveAt index
                Ok true

    let reorderTemplatesDependency: ReorderTemplates =
        fun _ templateIds ->
            templateIds
            |> List.iteri (fun index templateId ->
                match rows |> Seq.tryFindIndex (fun row -> row.Id = templateId) with
                | None -> ()
                | Some rowIndex ->
                    let existing = rows.[rowIndex]
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

    let loadSuppliersForTemplates: LoadSuppliersForTemplates = fun () -> Ok [ supplier ]

    let toException = TemplateApiMappers.toMyDogsbodyException

    test
        {
            GetTemplatesForSupplier =
                fun supplierIdString ->
                    ListTemplatesWorkflow.listTemplates loadTemplatesForSupplier supplierIdString
                    |> Result.map (List.map TemplateApiMappers.toUiType)
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.getTemplatesForSupplier)

            AddTemplate =
                fun uiType ->
                    uiType
                    |> TemplateApiMappers.toUnvalidatedTemplate
                    |> Result.bind (AddTemplateWorkflow.addTemplate loadSuppliersForTemplates loadTemplatesForSupplier saveTemplate)
                    |> Result.map ignore
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.addTemplate)

            EditTemplate =
                fun uiType ->
                    uiType
                    |> TemplateApiMappers.toUnvalidatedTemplateEdit
                    |> Result.bind (fun (id, unvalidated) ->
                        EditTemplateWorkflow.editTemplate loadTemplatesForSupplier updateTemplate id unvalidated)
                    |> Result.map ignore
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.editTemplate)

            DeleteTemplate =
                fun idString ->
                    DeleteTemplateWorkflow.deleteTemplate deleteTemplateDependency idString
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.deleteTemplate)

            ReorderTemplates =
                fun supplierIdString templateIdStrings ->
                    ReorderTemplatesWorkflow.reorderTemplates
                        loadTemplatesForSupplier
                        reorderTemplatesDependency
                        supplierIdString
                        templateIdStrings
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.reorderTemplates)

            // Never touches storage - reuses TemplateApiFactory's own glue rather than duplicating
            // it, per this file's header comment.
            TestTemplate =
                fun input ->
                    result {
                        let! validated =
                            input.Template
                            |> TemplateApiMappers.toUnvalidatedTemplate
                            |> Result.bind ValidateTemplateWorkflow.validateTemplate
                            |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.testTemplate)

                        let message = TemplateApiFactory.toTestMessage (ValidTemplate.part validated) input
                        let noTerm = PaymentTermDays.create 0 |> Result.defaultWith (fun _ -> failwith "unreachable: 0 is always in range")
                        let placeholderId = TemplateId.create "test" |> Result.defaultWith (fun _ -> failwith "unreachable: constant id")

                        let extracted = ApplyTemplateWorkflow.applyTemplate noTerm placeholderId validated message
                        let normalizedText =
                            TextNormalization.normalize (TemplateApiFactory.splitPastedTextIntoLines input.SampleText)
                            |> List.map (fun line -> line.Text)
                            |> String.concat "\n"

                        return
                            {
                                NormalizedText = normalizedText
                                FieldResults = [ Reference; Amount; Currency; IssueDate; DueDate ] |> List.map (TemplateApiFactory.toFieldTestResult extracted)
                            }
                    }
        }
        supplierIdString

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq =
    [
        [| box "real api" |]
        [| box "fake api" |]
    ]

let private withImplementation (name: string) (test: TemplateApi -> string -> unit) =
    match name with
    | "real api" -> withRealApi test
    | "fake api" -> withFakeApi test
    | other -> failwith $"Unknown implementation '{other}'"

let private validRules: TemplateFieldRuleUiType list =
    [
        { Field = "Reference"; RuleKind = "AfterLabel"; RuleText = "Invoice:"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsText"; HintText = "" }
        { Field = "Amount"; RuleKind = "AfterLabel"; RuleText = "Total:"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsMoney"; HintText = "." }
        { Field = "Currency"; RuleKind = "FixedValue"; RuleText = "AUD"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsText"; HintText = "" }
    ]

let private aTemplate supplierIdString : TemplateUiTypeWithoutId =
    {
        SupplierId = supplierIdString
        Name = "Monthly statement"
        DocumentPart = "AnyPart"
        AttachmentFormat = ""
        Position = 0
        Rules = validRules
    }

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
let ``GetTemplatesForSupplier returns an empty list before anything is added`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        Assert.Empty(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddTemplate stores every field and assigns it the next position`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        api.AddTemplate (aTemplate supplierIdString) |> okOrFail "AddTemplate"

        let stored = Assert.Single(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")
        Assert.False(String.IsNullOrWhiteSpace stored.Id)
        Assert.Equal(supplierIdString, stored.SupplierId)
        Assert.Equal("Monthly statement", stored.Name)
        Assert.Equal("AnyPart", stored.DocumentPart)
        Assert.Equal(0, stored.Position)
        Assert.Equal<string list>([ "Reference"; "Amount"; "Currency" ], stored.Rules |> List.map (fun r -> r.Field))
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``a second AddTemplate is positioned after the first`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        api.AddTemplate (aTemplate supplierIdString) |> okOrFail "AddTemplate"
        api.AddTemplate { aTemplate supplierIdString with Name = "Second" } |> okOrFail "AddTemplate"

        let stored = api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier"
        Assert.Equal<int list>([ 0; 1 ], stored |> List.map (fun t -> t.Position) |> List.sort)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``EditTemplate changes the addressed template and replaces its rules`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        api.AddTemplate (aTemplate supplierIdString) |> okOrFail "AddTemplate"
        let stored = Assert.Single(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")

        let smallerRules =
            [
                { Field = "Reference"; RuleKind = "AfterLabel"; RuleText = "Ref:"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsText"; HintText = "" }
                { Field = "Amount"; RuleKind = "AfterLabel"; RuleText = "Owed:"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsMoney"; HintText = "." }
                { Field = "Currency"; RuleKind = "FixedValue"; RuleText = "USD"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsText"; HintText = "" }
            ]

        api.EditTemplate { stored with Name = "Renamed"; Rules = smallerRules } |> okOrFail "EditTemplate"

        let reloaded = Assert.Single(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")
        Assert.Equal(stored.Id, reloaded.Id)
        Assert.Equal("Renamed", reloaded.Name)
        Assert.Equal("USD", (reloaded.Rules |> List.find (fun r -> r.Field = "Currency")).RuleText)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DeleteTemplate removes the addressed template`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        api.AddTemplate (aTemplate supplierIdString) |> okOrFail "AddTemplate"
        let stored = Assert.Single(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")

        api.DeleteTemplate stored.Id |> okOrFail "DeleteTemplate"

        Assert.Empty(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``ReorderTemplates persists the new order`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        api.AddTemplate (aTemplate supplierIdString) |> okOrFail "AddTemplate"
        api.AddTemplate { aTemplate supplierIdString with Name = "Second" } |> okOrFail "AddTemplate"
        let stored = api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier"
        let first = stored |> List.find (fun t -> t.Name = "Monthly statement")
        let second = stored |> List.find (fun t -> t.Name = "Second")

        api.ReorderTemplates supplierIdString [ second.Id; first.Id ] |> okOrFail "ReorderTemplates"

        let reordered =
            api.GetTemplatesForSupplier supplierIdString
            |> okOrFail "GetTemplatesForSupplier"
            |> List.sortBy (fun t -> t.Position)

        Assert.Equal<string list>([ second.Id; first.Id ], reordered |> List.map (fun t -> t.Id))
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddTemplate rejects an empty name with the documented message`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        let actual = api.AddTemplate { aTemplate supplierIdString with Name = "   " } |> errorOrFail "AddTemplate"

        Assert.Equal("Template name must not be empty.", actual.Message)
        Assert.Empty(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddTemplate rejects a supplier id that does not exist`` (implementation: string) =
    withImplementation implementation (fun api _ ->
        let actual = api.AddTemplate { aTemplate "9999" with SupplierId = "9999" } |> errorOrFail "AddTemplate"

        Assert.Equal("No supplier was found with id '9999'.", actual.Message)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddTemplate rejects a required field with no rule`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        let missingAmount = validRules |> List.filter (fun r -> r.Field <> "Amount")

        let actual = api.AddTemplate { aTemplate supplierIdString with Rules = missingAmount } |> errorOrFail "AddTemplate"

        Assert.Equal("Amount needs a rule.", actual.Message)
        Assert.Empty(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddTemplate accepts a template with no Currency rule - Currency is not required`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        let noCurrency = validRules |> List.filter (fun r -> r.Field <> "Currency")

        api.AddTemplate { aTemplate supplierIdString with Rules = noCurrency } |> okOrFail "AddTemplate"

        let stored = Assert.Single(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")
        Assert.DoesNotContain(stored.Rules, fun r -> r.Field = "Currency")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``EditTemplate reports not found for an unknown identifier`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        api.AddTemplate (aTemplate supplierIdString) |> okOrFail "AddTemplate"

        let actual =
            api.EditTemplate { Id = "9999"; SupplierId = supplierIdString; Name = "Ghost"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = validRules }
            |> errorOrFail "EditTemplate"

        Assert.Equal("No template was found with id '9999'.", actual.Message)
        Assert.Equal("Monthly statement", (Assert.Single(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")).Name)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DeleteTemplate reports not found for an unknown identifier`` (implementation: string) =
    withImplementation implementation (fun api _ ->
        let actual = api.DeleteTemplate "9999" |> errorOrFail "DeleteTemplate"

        Assert.Equal("No template was found with id '9999'.", actual.Message)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``ReorderTemplates reports an incomplete order`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        api.AddTemplate (aTemplate supplierIdString) |> okOrFail "AddTemplate"
        api.AddTemplate { aTemplate supplierIdString with Name = "Second" } |> okOrFail "AddTemplate"
        let stored = api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier"
        let first = stored |> List.find (fun t -> t.Name = "Monthly statement")

        let actual = api.ReorderTemplates supplierIdString [ first.Id ] |> errorOrFail "ReorderTemplates"

        Assert.Contains("missing template", actual.Message)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``TestTemplate reports extraction results without writing anything`` (implementation: string) =
    withImplementation implementation (fun api supplierIdString ->
        let input: TemplateTestInputUiType =
            {
                Template = aTemplate supplierIdString
                SampleText = "Invoice: INV-1\nTotal: 42.50"
                SampleSubject = ""
                SampleAttachmentFilename = ""
            }

        let result = api.TestTemplate input |> okOrFail "TestTemplate"

        let reference = result.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.True reference.Succeeded
        Assert.Equal("INV-1", reference.ParsedValue)
        Assert.Empty(api.GetTemplatesForSupplier supplierIdString |> okOrFail "GetTemplatesForSupplier")
    )
