module MyDogsbody.Tests.Database.TemplateStoreTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open Dapper.FSharp.SQLite
open MyDogsbody.Builders
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Database.Models

/// No-op logger, so these tests never reach Logging.db.
let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private validTemplate supplierId (rules: TemplateFieldRule list) : ValidTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = supplierId; Name = "Monthly statement"; Part = AnyPart; Position = 0; Rules = rules }

    match ValidateTemplateWorkflow.validateTemplate unvalidated with
    | Ok template -> template
    | Error error -> failwith $"Test setup produced an invalid template: {error}"

let private sampleRules =
    [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
      { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
      { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]

/// Fresh disposable SQLite database per test, schema built by the real migrations - never
/// hand-written DDL, so this doubles as a check that the migrations produce a usable schema.
let private withDatabase (test: DatabaseContext -> unit) =
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

/// Inserts a supplier directly (bypassing SupplierStore, so this file has no cross-store
/// dependency) and returns its row id as a string, ready for a template's SupplierId.
let private insertSupplierReturningId (context: DatabaseContext) (name: string) : string =
    let connection = context.GetDatabaseConnection()
    connection.Open()

    try
        use command = connection.CreateCommand()
        command.CommandText <-
            $"INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('{name}', 30);
              SELECT last_insert_rowid();"
        string (Convert.ToInt64(command.ExecuteScalar()))
    finally
        connection.Close()

// ---------- integration: round trips against a real SQLite file ----------

[<Fact; Trait("Level", "Integration")>]
let ``getForSupplier returns an empty list when the supplier has no templates`` () =
    withDatabase (fun context ->
        let supplierId = insertSupplierReturningId context "Acme" |> SupplierId.create |> valueOrFail

        let actual =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                supplierId
            |> okOrFail "getForSupplier"

        Assert.Empty actual
    )

[<Fact; Trait("Level", "Integration")>]
let ``insertOne stores a template and getForSupplier returns it with its id surfaced as a string and every rule attached`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        let template = validTemplate supplierIdString sampleRules

        let inserted =
            TemplateStore.insertOne
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                template
            |> okOrFail "insertOne"

        Assert.False(String.IsNullOrWhiteSpace(TemplateId.value inserted.Id))
        Assert.Equal("Monthly statement", TemplateName.value (ValidTemplate.name inserted.Template))
        Assert.Equal<TargetField list>(
            [ Reference; Amount; Currency ],
            (ValidTemplate.rules inserted.Template) |> List.map (fun r -> r.Field)
        )

        let stored =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"

        let readBack = Assert.Single stored
        Assert.Equal(TemplateId.value inserted.Id, TemplateId.value readBack.Id)
        Assert.Equal<TargetField list>(
            [ Reference; Amount; Currency ],
            (ValidTemplate.rules readBack.Template) |> List.map (fun r -> r.Field)
        )
    )

/// Every rule kind, target field and parse hint the domain model allows in one round trip -
/// TemplateRecordMappersTests already proves each case's column encoding in isolation; this
/// proves the whole pipeline (domain -> store -> SQLite -> store -> domain) survives all of them
/// together, including a compiled Regex that still matches after being recompiled from storage.
[<Fact; Trait("Level", "Integration")>]
let ``insertOne and getForSupplier round trip every rule kind, target field and parse hint`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        let template =
            validTemplate
                supplierIdString
                [ { Field = Reference; Rule = RegexCapture @"INV-(\d+)"; Hint = AsText }
                  { Field = Amount; Rule = LinesAfterLabel("Total", 1); Hint = AsMoney ',' }
                  { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
                  { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" }
                  { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ]

        TemplateStore.insertOne
            handleError
            context.GetDatabaseConnection
            context.GetInvoiceTemplates
            context.GetTemplateFieldRules
            template
        |> okOrFail "insertOne"
        |> ignore

        let readBack =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"
            |> List.exactlyOne

        let rules = ValidTemplate.rules readBack.Template
        Assert.Equal<TargetField list>([ Reference; Amount; Currency; IssueDate; DueDate ], rules |> List.map (fun r -> r.Field))
        Assert.Equal(RegexCapture @"INV-(\d+)", (rules |> List.find (fun r -> r.Field = Reference)).Rule)
        Assert.Equal(LinesAfterLabel("Total", 1), (rules |> List.find (fun r -> r.Field = Amount)).Rule)
        Assert.Equal(AsMoney ',', (rules |> List.find (fun r -> r.Field = Amount)).Hint)
        Assert.Equal(FixedValue "AUD", (rules |> List.find (fun r -> r.Field = Currency)).Rule)
        Assert.Equal(AfterLabel "Date:", (rules |> List.find (fun r -> r.Field = IssueDate)).Rule)
        Assert.Equal(AsDate "d MMM yyyy", (rules |> List.find (fun r -> r.Field = IssueDate)).Hint)
        Assert.Equal(DateFromField IssueDate, (rules |> List.find (fun r -> r.Field = DueDate)).Rule)

        let compiled = ValidTemplate.compiledPatterns readBack.Template
        let regexMatch = (Map.find Reference compiled).Match "INV-4471"
        Assert.True regexMatch.Success
        Assert.Equal("4471", regexMatch.Groups.[1].Value)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne changes the addressed row and a re-read reflects the replaced rule set`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        let inserted =
            validTemplate supplierIdString sampleRules
            |> TemplateStore.insertOne handleError context.GetDatabaseConnection context.GetInvoiceTemplates context.GetTemplateFieldRules
            |> okOrFail "insertOne"

        let replacementRules =
            [ { Field = Reference; Rule = AfterLabel "Ref:"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Owed:"; Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "USD"; Hint = AsText } ]
        let edited = validTemplate supplierIdString replacementRules

        let updated =
            TemplateStore.updateOne
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                inserted.Id
                edited
            |> okOrFail "updateOne"

        match updated with
        | Some stored ->
            Assert.Equal(FixedValue "USD", (ValidTemplate.rules stored.Template |> List.find (fun r -> r.Field = Currency)).Rule)
        | None -> Assert.Fail("Expected the row to be found")

        let reread =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"
            |> List.exactlyOne

        Assert.Equal(3, (ValidTemplate.rules reread.Template).Length)
        Assert.Equal(FixedValue "USD", (ValidTemplate.rules reread.Template |> List.find (fun r -> r.Field = Currency)).Rule)
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne reports not found for an identifier no row carries`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        let ghostId = TemplateId.create "9999" |> valueOrFail

        let updated =
            TemplateStore.updateOne
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                ghostId
                (validTemplate supplierIdString sampleRules)
            |> okOrFail "updateOne"

        Assert.True(Option.isNone updated, "expected None for an unknown identifier")
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleteOne removes the template and its field rules, both gone afterwards`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        let inserted =
            validTemplate supplierIdString sampleRules
            |> TemplateStore.insertOne handleError context.GetDatabaseConnection context.GetInvoiceTemplates context.GetTemplateFieldRules
            |> okOrFail "insertOne"

        let deleted =
            TemplateStore.deleteOne handleError context.GetDatabaseConnection context.GetInvoiceTemplates inserted.Id
            |> okOrFail "deleteOne"

        Assert.True deleted

        let stored =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"

        Assert.Empty stored
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleteOne reports not found for an identifier no row carries`` () =
    withDatabase (fun context ->
        let deleted =
            TemplateStore.deleteOne
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                (TemplateId.create "9999" |> valueOrFail)
            |> okOrFail "deleteOne"

        Assert.False deleted
    )

[<Fact; Trait("Level", "Integration")>]
let ``deleting a supplier deletes its templates and their field rules through the database cascade`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        validTemplate supplierIdString sampleRules
        |> TemplateStore.insertOne handleError context.GetDatabaseConnection context.GetInvoiceTemplates context.GetTemplateFieldRules
        |> okOrFail "insertOne"
        |> ignore

        let connection = context.GetDatabaseConnection()
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- $"DELETE FROM Suppliers WHERE Id = {supplierIdString};"
        command.ExecuteNonQuery() |> ignore
        connection.Close()

        let stored =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"

        Assert.Empty stored
    )

[<Fact; Trait("Level", "Integration")>]
let ``reorder persists the new order`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        let insertOne () =
            validTemplate supplierIdString sampleRules
            |> TemplateStore.insertOne handleError context.GetDatabaseConnection context.GetInvoiceTemplates context.GetTemplateFieldRules
            |> okOrFail "insertOne"
        let first = insertOne ()
        let second = insertOne ()
        let third = insertOne ()

        TemplateStore.reorder
            handleError
            context.GetDatabaseConnection
            context.GetInvoiceTemplates
            (SupplierId.create supplierIdString |> valueOrFail)
            [ third.Id; first.Id; second.Id ]
        |> okOrFail "reorder"

        let reread =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"
            |> List.sortBy (fun t -> ValidTemplate.position t.Template)

        Assert.Equal<string list>(
            [ TemplateId.value third.Id; TemplateId.value first.Id; TemplateId.value second.Id ],
            reread |> List.map (fun t -> TemplateId.value t.Id)
        )
    )

// ---------- integration: atomicity - a failure partway through leaves no trace ----------
//
// Each trigger is a genuine mid-write SQL failure, not a simulated one: insertOne/updateOne use
// a duplicate-field template (built via ValidateTemplateWorkflow.reconstructValidTemplate, which
// unlike validateTemplate does not check for duplicates - the only way to construct one, since
// validateTemplate itself refuses this template shape and the atomicity test needs a guaranteed
// unique-index violation on the SECOND field-rule insert). reorder's trigger is an unparseable
// TemplateId string, which TemplateRecordMappers.toRowId raises on - the same "data-integrity
// failure, allowed to raise" idiom already established for SupplierRecordMappers.toRowId.

// Every field's rule text differs from sampleRules on purpose: if a partial commit ever leaked
// through (the defect these tests exist to catch), a re-read would show THIS text mixed into the
// row, not merely the same three field names sampleRules also happens to use.
let private duplicateFieldTemplate supplierId =
    let rules =
        [ { Field = Reference; Rule = AfterLabel "PathRef:"; Hint = AsText }
          { Field = Amount; Rule = AfterLabel "PathTotal:"; Hint = AsMoney '.' }
          { Field = Currency; Rule = FixedValue "USD"; Hint = AsText }
          { Field = Amount; Rule = AfterLabel "PathBalance:"; Hint = AsMoney '.' } ] // duplicate field

    match
        ValidateTemplateWorkflow.reconstructValidTemplate
            (SupplierId.create supplierId |> valueOrFail)
            (TemplateName.create "Pathological" |> valueOrFail)
            AnyPart
            0
            rules
    with
    | Ok template -> template
    | Error error -> failwith $"Test setup could not reconstruct a template: {error}"

[<Fact; Trait("Level", "Integration")>]
let ``insertOne leaves no template row behind when a field-rule insert fails partway through`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"

        let actual =
            TemplateStore.insertOne
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (duplicateFieldTemplate supplierIdString)

        match actual with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected the unique index on (TemplateId, TargetField) to refuse the duplicate")

        let stored =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"

        Assert.Empty stored // no orphaned template row from the aborted insert
    )

[<Fact; Trait("Level", "Integration")>]
let ``updateOne leaves the original row untouched when a field-rule insert fails partway through`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        let inserted =
            validTemplate supplierIdString sampleRules
            |> TemplateStore.insertOne handleError context.GetDatabaseConnection context.GetInvoiceTemplates context.GetTemplateFieldRules
            |> okOrFail "insertOne"

        let actual =
            TemplateStore.updateOne
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                inserted.Id
                (duplicateFieldTemplate supplierIdString)

        match actual with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected the unique index on (TemplateId, TargetField) to refuse the duplicate")

        let reread =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"
            |> List.exactlyOne

        // The ORIGINAL row, untouched - both the name (which duplicateFieldTemplate sets to
        // "Pathological", different from "Monthly statement") and every rule's own text (which
        // duplicateFieldTemplate also gives different labels) prove nothing from the aborted
        // attempt survived, not merely that the three field NAMES happen to coincide.
        Assert.Equal("Monthly statement", TemplateName.value (ValidTemplate.name reread.Template))
        Assert.Equal<TargetField list>(
            [ Reference; Amount; Currency ],
            (ValidTemplate.rules reread.Template) |> List.map (fun r -> r.Field)
        )
        Assert.Equal(AfterLabel "Invoice:", (ValidTemplate.rules reread.Template |> List.find (fun r -> r.Field = Reference)).Rule)
        Assert.Equal(FixedValue "AUD", (ValidTemplate.rules reread.Template |> List.find (fun r -> r.Field = Currency)).Rule)
    )

[<Fact; Trait("Level", "Integration")>]
let ``reorder leaves every position untouched when a later id fails to parse partway through`` () =
    withDatabase (fun context ->
        let supplierIdString = insertSupplierReturningId context "Acme"
        let insertOne () =
            validTemplate supplierIdString sampleRules
            |> TemplateStore.insertOne handleError context.GetDatabaseConnection context.GetInvoiceTemplates context.GetTemplateFieldRules
            |> okOrFail "insertOne"
        // insertOne does not assign incrementing positions itself (that is AddTemplateWorkflow's
        // job, upstream of the store) - all three start at Position 0. A real reorder first, here,
        // is what establishes known distinct positions to test the pathological attempt against.
        let first = insertOne ()
        let second = insertOne ()
        let third = insertOne ()
        let supplierId = SupplierId.create supplierIdString |> valueOrFail

        TemplateStore.reorder handleError context.GetDatabaseConnection context.GetInvoiceTemplates supplierId [ first.Id; second.Id; third.Id ]
        |> okOrFail "reorder (establishing positions)"
        // first=0, second=1, third=2

        let unparseableId = TemplateId.create "not-a-number" |> valueOrFail

        let actual =
            TemplateStore.reorder
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                supplierId
                // third's update (to position 0) runs and would succeed before the failure -
                // this is the one a partial commit would actually leak, so it is what gets
                // checked directly below rather than trusted to a sorted-list comparison, which
                // a tie in Position could make look correct even when it is not.
                [ third.Id; unparseableId; first.Id; second.Id ]

        match actual with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected the unparseable id to fail the reorder")

        let rereadThird =
            TemplateStore.getForSupplier
                handleError
                context.GetDatabaseConnection
                context.GetInvoiceTemplates
                context.GetTemplateFieldRules
                (SupplierId.create supplierIdString |> valueOrFail)
            |> okOrFail "getForSupplier"
            |> List.find (fun t -> t.Id = third.Id)

        // third's position must still be its ORIGINAL 2, not the 0 the aborted reorder attempted
        Assert.Equal(2, ValidTemplate.position rereadThird.Template)
    )

// ---------- unit: error paths, no database reached ----------

let private failingConnection () : SqliteConnection = raise (InvalidOperationException "database is gone")
let private failingTemplates () : QuerySource<InvoiceTemplateRecord> = raise (InvalidOperationException "database is gone")
let private failingRules () : QuerySource<TemplateFieldRuleRecord> = raise (InvalidOperationException "database is gone")

let private aValidTemplate = validTemplate "1" sampleRules
let private aTemplateId = TemplateId.create "1" |> valueOrFail
let private aSupplierId = SupplierId.create "1" |> valueOrFail

[<Fact; Trait("Level", "Unit")>]
let ``getForSupplier reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual = TemplateStore.getForSupplier recordingHandleError failingConnection failingTemplates failingRules aSupplierId

    match actual with
    | Error ex ->
        Assert.Equal(MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.TemplateStore.getForSupplier, ex.ActionName)
        Assert.Equal("Failed to retrieve templates for supplier.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``insertOne reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual = TemplateStore.insertOne recordingHandleError failingConnection failingTemplates failingRules aValidTemplate

    match actual with
    | Error ex ->
        Assert.Equal(MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.TemplateStore.insertOne, ex.ActionName)
        Assert.Equal("Failed to insert new template.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``updateOne reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual =
        TemplateStore.updateOne recordingHandleError failingConnection failingTemplates failingRules aTemplateId aValidTemplate

    match actual with
    | Error ex ->
        Assert.Equal(MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.TemplateStore.updateOne, ex.ActionName)
        Assert.Equal("Failed to update existing template.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``deleteOne reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual = TemplateStore.deleteOne recordingHandleError failingConnection failingTemplates aTemplateId

    match actual with
    | Error ex ->
        Assert.Equal(MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.TemplateStore.deleteOne, ex.ActionName)
        Assert.Equal("Failed to delete existing template.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``reorder reports a MyDogsbodyException carrying its declared action when the connection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbody.Exceptions.Types.MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let actual = TemplateStore.reorder recordingHandleError failingConnection failingTemplates aSupplierId [ aTemplateId ]

    match actual with
    | Error ex ->
        Assert.Equal(MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.TemplateStore.reorder, ex.ActionName)
        Assert.Equal("Failed to persist the new template order.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
