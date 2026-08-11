module MyDogsbody.Tests.Contracts.TemplatePersistedShapeTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Database

// SQLite is not schemaless the way LiteDB is, but a Dapper.FSharp record field renamed without a
// migration fails at run time only - so the persisted names are asserted here by reading the
// table schema, not just by round-tripping an object. The split-column encoding for
// FieldRule/ParseHint/DocumentPart is the reason a mapper is worth asserting field-for-field, not
// only "it round-trips": a case whose payload lands in the wrong column would still round-trip
// through the SAME mapper that put it there, so the migration's own column list is the source of
// truth this file checks against.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private validOrFail (result: Result<'T, TemplateError>) =
    match result with
    | Ok value -> value
    | Error error -> failwith $"Test setup built an invalid template: {error}"

let private withMigratedDatabase (test: DatabaseContext -> string -> string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath}"
    MyDogsbody.Database.Migrations.MigrationSetup.setupMigrations connectionString
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
        test context connectionString supplierIdString
    finally
        context.Dispose()
        SqliteConnection.ClearAllPools()
        File.Delete databaseFilePath

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
let ``an invoice template is persisted under the documented column names`` () =
    withMigratedDatabase (fun _ connectionString _ ->
        Assert.Equal<string list>(
            [ "Id"; "SupplierId"; "Name"; "DocumentPart"; "AttachmentFormat"; "Position" ],
            columnNames connectionString "InvoiceTemplates"
        )
    )

[<Fact; Trait("Level", "Contract")>]
let ``a template field rule is persisted under the documented column names`` () =
    withMigratedDatabase (fun _ connectionString _ ->
        Assert.Equal<string list>(
            [ "Id"; "TemplateId"; "TargetField"; "RuleKind"; "RuleText"; "RuleOffset"; "RuleSourceField"; "HintKind"; "HintText" ],
            columnNames connectionString "TemplateFieldRules"
        )
    )

let private aTemplate supplierIdString (part: DocumentPart) (rules: TemplateFieldRule list) : UnvalidatedTemplate =
    {
        SupplierId = supplierIdString
        Name = "Monthly statement"
        Part = part
        Position = 0
        Rules = rules
    }

let private requiredRules =
    [
        { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
        { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
        { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
    ]

let private insert context supplierIdString part rules =
    aTemplate supplierIdString part rules
    |> ValidateTemplateWorkflow.validateTemplate
    |> Result.mapError string
    |> Result.bind (fun validated ->
        TemplateStore.insertOne
            handleError
            (context: DatabaseContext).GetDatabaseConnection
            context.GetInvoiceTemplates
            context.GetTemplateFieldRules
            validated
        |> Result.mapError (fun ex -> ex.Message))
    |> valueOrFail

[<Fact; Trait("Level", "Contract")>]
let ``a TemplateName at the maximum length survives the round trip unchanged`` () =
    withMigratedDatabase (fun context _ supplierIdString ->
        let atLimit = String('a', TemplateName.MaximumLength)

        let renamed: UnvalidatedTemplate =
            { SupplierId = supplierIdString; Name = atLimit; Part = AnyPart; Position = 0; Rules = requiredRules }

        let validated = ValidateTemplateWorkflow.validateTemplate renamed |> validOrFail

        let stored =
            TemplateStore.insertOne
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                validated
            |> Result.mapError (fun ex -> ex.Message)
            |> valueOrFail

        Assert.Equal(atLimit, TemplateName.value (ValidTemplate.name stored.Template))
        Assert.Equal(TemplateName.MaximumLength, (TemplateName.value (ValidTemplate.name stored.Template)).Length)
    )

[<Fact; Trait("Level", "Contract")>]
let ``an awkward template name survives the round trip unchanged, including non-ASCII`` () =
    withMigratedDatabase (fun context _ supplierIdString ->
        let awkward = "Société Générale — Facturation mensuelle"

        let renamed: UnvalidatedTemplate =
            { SupplierId = supplierIdString; Name = awkward; Part = AnyPart; Position = 0; Rules = requiredRules }

        let validated = ValidateTemplateWorkflow.validateTemplate renamed |> validOrFail

        let stored =
            TemplateStore.insertOne
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                validated
            |> Result.mapError (fun ex -> ex.Message)
            |> valueOrFail

        Assert.Equal(awkward, TemplateName.value (ValidTemplate.name stored.Template))
    )

[<Theory; Trait("Level", "Contract")>]
[<InlineData("Body")>]
[<InlineData("AnyPart")>]
[<InlineData("Attachment")>]
let ``every DocumentPart case survives the round trip`` (caseName: string) =
    withMigratedDatabase (fun context _ supplierIdString ->
        let part =
            match caseName with
            | "Body" -> Body
            | "AnyPart" -> AnyPart
            | "Attachment" -> Attachment Pdf
            | other -> failwith $"Unhandled test case {other}"

        let stored = insert context supplierIdString part requiredRules

        Assert.Equal(part, ValidTemplate.part stored.Template)
    )

/// One rule per FieldRule kind, each in its own fully-valid rule set (Reference/Amount/Currency
/// still covered, so validateTemplate accepts it) - asserted by round-tripping the whole rule, not
/// only its kind, so a payload landing in the wrong split column is still caught. Written out
/// longhand per case rather than derived generically, since a "helpful" generic builder would be
/// exactly the kind of thing that could quietly produce an invalid combination.
[<Fact; Trait("Level", "Contract")>]
let ``every FieldRule kind survives the round trip with its payload intact`` () =
    withMigratedDatabase (fun context _ supplierIdString ->
        let baseline = [ Amount, AfterLabel "Total:", AsMoney '.'; Currency, FixedValue "AUD", AsText ]

        let withReferenceRule (rule: FieldRule) =
            { Field = Reference; Rule = rule; Hint = AsText }
            :: (baseline |> List.map (fun (field, rule, hint) -> { Field = field; Rule = rule; Hint = hint }))

        let cases: (TargetField * TemplateFieldRule list) list =
            [
                Reference, withReferenceRule (AfterLabel "Invoice:")
                Reference, withReferenceRule (LinesAfterLabel("Invoice:", 2))
                Reference, withReferenceRule (RegexCapture @"Invoice #(\d+)")
                Reference, withReferenceRule (FixedValue "N/A")
                Reference, withReferenceRule (SubjectCapture @"Re: (.+)")
                Reference, withReferenceRule (AttachmentName @"invoice-(\d+)\.pdf")
                IssueDate,
                    [
                        { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
                        { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
                        { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
                        { Field = DueDate; Rule = AfterLabel "Due:"; Hint = AsDate "yyyy-MM-dd" }
                        { Field = IssueDate; Rule = DateFromField DueDate; Hint = AsDate "yyyy-MM-dd" }
                    ]
            ]

        for targetField, rules in cases do
            let expected = rules |> List.find (fun r -> r.Field = targetField)
            let stored = insert context supplierIdString AnyPart rules
            let readBack = ValidTemplate.rules stored.Template |> List.find (fun r -> r.Field = targetField)

            Assert.Equal(expected.Rule, readBack.Rule)
            Assert.Equal(expected.Hint, readBack.Hint)
    )

[<Fact; Trait("Level", "Contract")>]
let ``every ParseHint kind survives the round trip with its payload intact`` () =
    withMigratedDatabase (fun context _ supplierIdString ->
        let rules =
            [
                { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
                { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney ',' }
                { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
                { Field = IssueDate; Rule = AfterLabel "Issued:"; Hint = AsDate "dd/MM/yyyy" }
            ]

        let stored = insert context supplierIdString AnyPart rules
        let readBack = ValidTemplate.rules stored.Template

        Assert.Equal(AsText, (readBack |> List.find (fun r -> r.Field = Reference)).Hint)
        Assert.Equal(AsMoney ',', (readBack |> List.find (fun r -> r.Field = Amount)).Hint)
        Assert.Equal(AsDate "dd/MM/yyyy", (readBack |> List.find (fun r -> r.Field = IssueDate)).Hint)
    )
