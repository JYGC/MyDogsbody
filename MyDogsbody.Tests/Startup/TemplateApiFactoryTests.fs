module MyDogsbody.Tests.Startup.TemplateApiFactoryTests

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

let private handleError = HandleErrorBuilder (fun _ -> ())

let private uiRule field ruleKind ruleText ruleOffset ruleSourceField hintKind hintText : TemplateFieldRuleUiType =
    { Field = field; RuleKind = ruleKind; RuleText = ruleText; RuleOffset = ruleOffset; RuleSourceField = ruleSourceField; HintKind = hintKind; HintText = hintText }

let private validRulesUi =
    [ uiRule "Reference" "AfterLabel" "Invoice:" 0 "" "AsText" ""
      uiRule "Amount" "AfterLabel" "Total:" 0 "" "AsMoney" "."
      uiRule "Currency" "FixedValue" "AUD" 0 "" "AsText" "" ]

/// Fresh temp SQLite file per test, schema built by the real migrations, context disposed and
/// the file deleted - no test reaches Startup.Startup.
let private withApi (test: TemplateApi -> string -> unit) =
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath}"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath
    let api = TemplateApiFactory.createTemplateApi handleError context

    // A supplier must exist for AddTemplate/TemplateSupplierNotFound to be meaningful - inserted
    // directly, bypassing SupplierStore, so this file has no cross-store test dependency.
    let supplierId =
        let connection = context.GetDatabaseConnection()
        connection.Open()
        try
            use command = connection.CreateCommand()
            command.CommandText <- "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES ('Acme', 30); SELECT last_insert_rowid();"
            string (Convert.ToInt64(command.ExecuteScalar()))
        finally
            connection.Close()

    try
        test api supplierId
    finally
        context.Dispose()
        SqliteConnection.ClearAllPools()
        File.Delete databaseFilePath

let private aTemplate supplierId : TemplateUiTypeWithoutId =
    { SupplierId = supplierId; Name = "Monthly statement"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = validRulesUi }

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

let private errorOrFail label result =
    match result with
    | Error (ex: MyDogsbodyException) -> ex
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

let private single (api: TemplateApi) supplierId =
    match api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier" with
    | [ only ] -> only
    | other -> failwith $"Expected exactly one template, got {List.length other}"

[<Fact; Trait("Level", "Integration")>]
let ``GetTemplatesForSupplier returns an empty list for a supplier with no templates`` () =
    withApi (fun api supplierId -> Assert.Empty(api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier"))

[<Fact; Trait("Level", "Integration")>]
let ``AddTemplate stores every field and assigns an identifier`` () =
    withApi (fun api supplierId ->
        api.AddTemplate(aTemplate supplierId) |> okOrFail "AddTemplate"

        let stored = single api supplierId
        Assert.False(String.IsNullOrWhiteSpace stored.Id)
        Assert.Equal(supplierId, stored.SupplierId)
        Assert.Equal("Monthly statement", stored.Name)
        Assert.Equal<string list>([ "Reference"; "Amount"; "Currency" ], stored.Rules |> List.map (fun r -> r.Field))
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditTemplate changes the addressed template and replaces its rule set`` () =
    withApi (fun api supplierId ->
        api.AddTemplate(aTemplate supplierId) |> okOrFail "AddTemplate"
        let stored = single api supplierId

        api.EditTemplate
            { stored with
                Name = "Renamed"
                Rules = [ uiRule "Reference" "AfterLabel" "Ref:" 0 "" "AsText" ""
                          uiRule "Amount" "AfterLabel" "Owed:" 0 "" "AsMoney" "."
                          uiRule "Currency" "FixedValue" "USD" 0 "" "AsText" "" ] }
        |> okOrFail "EditTemplate"

        let reloaded = single api supplierId
        Assert.Equal(stored.Id, reloaded.Id)
        Assert.Equal("Renamed", reloaded.Name)
        Assert.Equal("USD", (reloaded.Rules |> List.find (fun r -> r.Field = "Currency")).RuleText)
    )

[<Fact; Trait("Level", "Integration")>]
let ``DeleteTemplate removes the template`` () =
    withApi (fun api supplierId ->
        api.AddTemplate(aTemplate supplierId) |> okOrFail "AddTemplate"
        let stored = single api supplierId

        api.DeleteTemplate stored.Id |> okOrFail "DeleteTemplate"

        Assert.Empty(api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier")
    )

[<Fact; Trait("Level", "Integration")>]
let ``ReorderTemplates persists the new order`` () =
    withApi (fun api supplierId ->
        api.AddTemplate { aTemplate supplierId with Name = "First" } |> okOrFail "AddTemplate"
        api.AddTemplate { aTemplate supplierId with Name = "Second" } |> okOrFail "AddTemplate"

        let stored = api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier"
        let firstId = (stored |> List.find (fun t -> t.Name = "First")).Id
        let secondId = (stored |> List.find (fun t -> t.Name = "Second")).Id

        api.ReorderTemplates supplierId [ secondId; firstId ] |> okOrFail "ReorderTemplates"

        let reread =
            api.GetTemplatesForSupplier supplierId
            |> okOrFail "GetTemplatesForSupplier"
            |> List.sortBy (fun t -> t.Position)

        Assert.Equal<string list>([ secondId; firstId ], reread |> List.map (fun t -> t.Id))
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddTemplate rejects a supplier that does not exist and stores nothing`` () =
    withApi (fun api supplierId ->
        let actual = api.AddTemplate { aTemplate "9999" with SupplierId = "9999" } |> errorOrFail "AddTemplate"

        Assert.Equal(ActionNames.MyDogsbody.Startup.TemplateApi.addTemplate, actual.ActionName)
        Assert.Equal("No supplier was found with id '9999'.", actual.Message)
        Assert.Empty(api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier")
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddTemplate rejects an empty name and stores nothing`` () =
    withApi (fun api supplierId ->
        let actual = api.AddTemplate { (aTemplate supplierId) with Name = "" } |> errorOrFail "AddTemplate"

        Assert.Equal("Template name must not be empty.", actual.Message)
        Assert.Empty(api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier")
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditTemplate reports not found when no template carries that id`` () =
    withApi (fun api supplierId ->
        let actual =
            api.EditTemplate
                { Id = "9999"; SupplierId = supplierId; Name = "Ghost"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = validRulesUi }
            |> errorOrFail "EditTemplate"

        Assert.Equal("No template was found with id '9999'.", actual.Message)
    )

[<Fact; Trait("Level", "Integration")>]
let ``DeleteTemplate reports not found when no template carries that id`` () =
    withApi (fun api _ ->
        let actual = api.DeleteTemplate "9999" |> errorOrFail "DeleteTemplate"

        Assert.Equal(ActionNames.MyDogsbody.Startup.TemplateApi.deleteTemplate, actual.ActionName)
        Assert.Equal("No template was found with id '9999'.", actual.Message)
    )

[<Fact; Trait("Level", "Integration")>]
let ``a validation failure is never written to the log`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let databaseFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={databaseFilePath}"
    MigrationSetup.setupMigrations connectionString
    let context = DatabaseContextSetup.createDatabaseContext databaseFilePath
    let api = TemplateApiFactory.createTemplateApi recordingHandleError context

    try
        api.AddTemplate { aTemplate "1" with Name = "" } |> ignore
        Assert.Empty logged
    finally
        context.Dispose()
        SqliteConnection.ClearAllPools()
        File.Delete databaseFilePath

[<Fact; Trait("Level", "Integration")>]
let ``a store failure reaches the UI as an Error and is written to the log exactly once`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let brokenContext: DatabaseContext =
        {
            GetDatabaseConnection = fun () -> raise (InvalidOperationException "database is gone")
            GetBlogs = fun () -> failwith "not used"
            GetComments = fun () -> failwith "not used"
            GetSuppliers = fun () -> failwith "not used"
            GetSupplierMatchers = fun () -> failwith "not used"
            GetInvoiceTemplates = fun () -> failwith "not used"
            GetTemplateFieldRules = fun () -> failwith "not used"
            Dispose = fun () -> ()
        }

    let api = TemplateApiFactory.createTemplateApi recordingHandleError brokenContext

    let actual = api.GetTemplatesForSupplier "1"

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Startup.TemplateApi.getTemplatesForSupplier, ex.ActionName)
        Assert.Equal("Failed to retrieve templates for supplier.", ex.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

    Assert.Single logged |> ignore

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate runs the same engine a scan calls, over pasted text, and returns the normalized text and per-field results`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            {
                Template = aTemplate supplierId
                SampleText = "Invoice: INV-9001\nTotal: 245.00"
                SampleSubject = ""
                SampleAttachmentFilename = ""
            }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        Assert.Contains("INV-9001", actual.NormalizedText)
        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.True referenceResult.Succeeded
        Assert.Equal("INV-9001", referenceResult.ParsedValue)
        let amountResult = actual.FieldResults |> List.find (fun r -> r.Field = "Amount")
        Assert.True amountResult.Succeeded
        Assert.Equal("245.00", amountResult.ParsedValue)
    )

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate never writes - the template is not stored`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId; SampleText = "Invoice: INV-1\nTotal: 1.00"; SampleSubject = ""; SampleAttachmentFilename = "" }

        api.TestTemplate input |> okOrFail "TestTemplate" |> ignore

        Assert.Empty(api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier")
    )

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate reports a failed field rather than a default when the sample text does not match`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId; SampleText = "nothing relevant here"; SampleSubject = ""; SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.False referenceResult.Succeeded
        Assert.False(String.IsNullOrWhiteSpace referenceResult.FailureReason)
    )

// PR #14 review: applyTemplate returns a single Result, and TestTemplate mapped that same value
// over all five fields - so one field's failure was rendered as all five failing, each carrying
// an F# union dump ("TemplateMatchedNothing (TemplateId \"test\", Amount)") rather than a
// sentence. The per-field diagnosis the panel exists for (Q7.6.6) was unavailable.

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate blames only the field whose rule failed, and does not report a field that matched as failed`` () =
    withApi (fun api supplierId ->
        // Reference matches; Amount has no "Total:" line to find.
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId
              SampleText = "Invoice: INV-9001\nnothing else of interest"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let amountResult = actual.FieldResults |> List.find (fun r -> r.Field = "Amount")
        Assert.False amountResult.Succeeded
        Assert.Equal("No text matched the rule for Amount.", amountResult.FailureReason)

        // Reference matched, so it must not carry Amount's failure.
        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.NotEqual<string>(amountResult.FailureReason, referenceResult.FailureReason)
        Assert.Equal(
            "Ran without error, but the run stopped at Amount before this value could be reported.",
            referenceResult.FailureReason
        )
    )

// PR #14 review round 3: applyTemplate evaluates in a fixed order (Reference, Amount, Currency,
// IssueDate, DueDate) and stops at the first failure, so every field BEFORE the blamed one has
// already run and succeeded. The panel told the user those fields were "Not evaluated" - which
// sends them to fix a rule that demonstrably works. Only the fields after the blamed one were
// genuinely never reached.

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate does not tell the user a field the engine already evaluated was never evaluated`` () =
    withApi (fun api supplierId ->
        // Reference is evaluated first and matches; Amount is evaluated next and stops the run.
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId
              SampleText = "Invoice: INV-9001\nnothing else of interest"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.DoesNotContain("Not evaluated", referenceResult.FailureReason)
        Assert.Contains("Ran without error", referenceResult.FailureReason)
        Assert.Contains("stopped at Amount", referenceResult.FailureReason)

        // Currency comes AFTER Amount in the evaluation order, so for it "not evaluated" is the
        // true answer and must stay.
        let currencyResult = actual.FieldResults |> List.find (fun r -> r.Field = "Currency")
        Assert.Equal("Not evaluated: the run stopped at Amount.", currencyResult.FailureReason)
    )

// PR #14 review round 3: TestTemplate hard-coded PaymentTermDays 0, so a DateFromField rule's
// derived due date always came back equal to the issue date - a plausible-looking wrong value
// with nothing in the panel saying which term produced it. requirements.md asks the panel to
// show "the derived due date, with the payment term it applied"; the supplier's real term is
// reachable through loadSuppliersForTemplates, already bound in this same factory.

let private dueDateFromIssueDateRules =
    [ uiRule "Reference" "AfterLabel" "Invoice:" 0 "" "AsText" ""
      uiRule "Amount" "AfterLabel" "Total:" 0 "" "AsMoney" "."
      uiRule "IssueDate" "AfterLabel" "Date:" 0 "" "AsDate" "yyyy-MM-dd"
      uiRule "DueDate" "DateFromField" "" 0 "IssueDate" "AsDate" "yyyy-MM-dd" ]

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate derives the due date with the supplier's own payment term and reports which term it applied`` () =
    withApi (fun api supplierId ->
        // withApi's supplier carries PaymentTermDays = 30.
        let input: TemplateTestInputUiType =
            { Template = { aTemplate supplierId with Rules = dueDateFromIssueDateRules }
              SampleText = "Invoice: INV-9001\nTotal: 245.00\nDate: 2026-08-16"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        Assert.Equal(30, actual.PaymentTermDaysApplied)

        let issueDateResult = actual.FieldResults |> List.find (fun r -> r.Field = "IssueDate")
        Assert.True issueDateResult.Succeeded
        Assert.Equal("2026-08-16", issueDateResult.ParsedValue)

        let dueDateResult = actual.FieldResults |> List.find (fun r -> r.Field = "DueDate")
        Assert.True dueDateResult.Succeeded
        Assert.Equal("2026-09-15", dueDateResult.ParsedValue)
    )

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate falls back to a zero payment term, and says so, when the template names no known supplier`` () =
    withApi (fun api _ ->
        // A supplier id that parses but names no row: the panel still has to answer for every
        // other field, so the term falls back rather than failing the run - and the reported
        // term is what makes that fallback visible instead of silent.
        let input: TemplateTestInputUiType =
            { Template = { aTemplate "999999" with Rules = dueDateFromIssueDateRules }
              SampleText = "Invoice: INV-9001\nTotal: 245.00\nDate: 2026-08-16"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        Assert.Equal(0, actual.PaymentTermDaysApplied)

        let dueDateResult = actual.FieldResults |> List.find (fun r -> r.Field = "DueDate")
        Assert.True dueDateResult.Succeeded
        Assert.Equal("2026-08-16", dueDateResult.ParsedValue)

        // The rest of the panel stays usable.
        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.True referenceResult.Succeeded
        Assert.Equal("INV-9001", referenceResult.ParsedValue)
    )

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate reports a failure as a sentence, never as a raw union case or the placeholder template id`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId
              SampleText = "nothing relevant here"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        for fieldResult in actual.FieldResults do
            Assert.DoesNotContain("TemplateMatchedNothing", fieldResult.FailureReason)
            Assert.DoesNotContain("TemplateId", fieldResult.FailureReason)
            Assert.DoesNotContain("\"test\"", fieldResult.FailureReason)
    )

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate names the unparseable value in the failing field's message`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId
              SampleText = "Invoice: INV-9001\nTotal: not-a-number"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let amountResult = actual.FieldResults |> List.find (fun r -> r.Field = "Amount")
        Assert.False amountResult.Succeeded
        Assert.Contains("not-a-number", amountResult.FailureReason)
    )

// PR #14 review: toTestMessage always put the pasted text under BodyPart, but an Attachment
// template's selector filters BodyPart out - so content.Lines was empty and every text rule
// reported "matched nothing", while the panel simultaneously rendered the normalized text and
// claimed nothing was found in it.

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate runs an Attachment template against the pasted text rather than against nothing`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = { aTemplate supplierId with DocumentPart = "Attachment"; AttachmentFormat = "Pdf" }
              SampleText = "Invoice: INV-7\nTotal: 12.50"
              SampleSubject = ""
              SampleAttachmentFilename = "statement.pdf" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.True(referenceResult.Succeeded, $"Reference should have matched, but failed with: {referenceResult.FailureReason}")
        Assert.Equal("INV-7", referenceResult.ParsedValue)
        let amountResult = actual.FieldResults |> List.find (fun r -> r.Field = "Amount")
        Assert.True(amountResult.Succeeded, $"Amount should have matched, but failed with: {amountResult.FailureReason}")
        Assert.Equal("12.50", amountResult.ParsedValue)
    )

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate runs an Attachment template against the pasted text even when no filename was chosen`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = { aTemplate supplierId with DocumentPart = "Attachment"; AttachmentFormat = "Pdf" }
              SampleText = "Invoice: INV-8\nTotal: 9.99"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.True(referenceResult.Succeeded, $"Reference should have matched, but failed with: {referenceResult.FailureReason}")
        Assert.Equal("INV-8", referenceResult.ParsedValue)
    )

[<Fact; Trait("Level", "Unit")>]
let ``toTestMessage gives an AnyPart template's attachment no lines, so the pasted text is never counted twice`` () =
    // AnyPart selects the body AND the attachment. If both carried the pasted lines, every rule
    // would see each line twice - so only the part the template actually selects gets them.
    let input: TemplateTestInputUiType =
        { Template = aTemplate "1"; SampleText = "Invoice: INV-1"; SampleSubject = ""; SampleAttachmentFilename = "statement.pdf" }

    let actual = TemplateApiFactory.toTestMessage MyDogsbody.Domain.InvoiceTemplates.AnyPart input

    let attachmentLines =
        actual.Parts
        |> List.pick (fun (part, lines) ->
            match part with
            | MyDogsbody.Domain.Invoices.AttachmentPart _ -> Some lines
            | _ -> None)

    Assert.Empty attachmentLines

// PR #14 review round 2: this change made Currency optional, so a template carrying no Currency
// rule now validates - and the panel rendered that row as "Failed: No value extracted.", the
// same sentence it uses for a rule that ran and matched nothing. Measured before the fix, a
// template whose Currency rule TIMED OUT and one with no Currency rule at all produced byte
// identical rows. The half that needs no domain change is separated here; see outcome.md for
// the half (timeout vs. not-found) that waits on change #4.

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate says a field has no rule rather than reporting it as an empty extraction`` () =
    withApi (fun api supplierId ->
        let noCurrency = validRulesUi |> List.filter (fun r -> r.Field <> "Currency")

        let input: TemplateTestInputUiType =
            { Template = { aTemplate supplierId with Rules = noCurrency }
              SampleText = "Invoice: INV-9001\nTotal: 245.00"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        // Currency is newly ruleless-legal here; IssueDate and DueDate never had rules.
        for field in [ "Currency"; "IssueDate"; "DueDate" ] do
            let fieldResult = actual.FieldResults |> List.find (fun r -> r.Field = field)
            Assert.False(fieldResult.Succeeded, $"{field} should not report success")
            Assert.Equal("No rule for this field.", fieldResult.FailureReason)
    )

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate still reports a rule that ran and matched nothing as an empty extraction`` () =
    withApi (fun api supplierId ->
        // The guard against over-correcting: a field that DOES carry a rule keeps the old
        // sentence, because a rule really did run and really did find nothing.
        let currencyByLabel =
            validRulesUi
            |> List.map (fun r -> if r.Field = "Currency" then uiRule "Currency" "AfterLabel" "Currency:" 0 "" "AsText" "" else r)

        let input: TemplateTestInputUiType =
            { Template = { aTemplate supplierId with Rules = currencyByLabel }
              SampleText = "Invoice: INV-9001\nTotal: 245.00"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let currencyResult = actual.FieldResults |> List.find (fun r -> r.Field = "Currency")
        Assert.False currencyResult.Succeeded
        Assert.Equal("No value extracted.", currencyResult.FailureReason)
    )

[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate does not claim a ruleless field was skipped when another field's rule failed`` () =
    withApi (fun api supplierId ->
        // On the Error path a ruleless field was blamed on whichever field stopped the run
        // ("Not evaluated: the run stopped at Amount."), which reads as though it would have
        // been evaluated. It never had a rule to evaluate.
        let input: TemplateTestInputUiType =
            { Template = { aTemplate supplierId with Rules = validRulesUi }
              SampleText = "Invoice: INV-9001\nnothing else of interest"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let issueDateResult = actual.FieldResults |> List.find (fun r -> r.Field = "IssueDate")
        Assert.Equal("No rule for this field.", issueDateResult.FailureReason)

        // Currency DOES carry a rule in validRulesUi, so it keeps the stopped-run wording.
        let currencyResult = actual.FieldResults |> List.find (fun r -> r.Field = "Currency")
        Assert.Equal("Not evaluated: the run stopped at Amount.", currencyResult.FailureReason)
    )
