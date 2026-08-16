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

/// A save-time refusal the top mapper had no branch for reaches the UI as an alertable Error.
/// An unmatched case raises MatchFailureException instead, out of an API the UI calls from
/// Async.Start - so this asserts the sentence, not merely that a Result came back.
[<Fact; Trait("Level", "Integration")>]
let ``AddTemplate refuses a rule that cannot reach the template's part, as an Error rather than a raise`` () =
    withApi (fun api supplierId ->
        let actual =
            api.AddTemplate
                { aTemplate supplierId with
                    DocumentPart = "Body"
                    Rules =
                        [ uiRule "Reference" "AttachmentName" @"(INV-\d+)" 0 "" "AsText" ""
                          uiRule "Amount" "AfterLabel" "Total:" 0 "" "AsMoney" "."
                          uiRule "Currency" "FixedValue" "AUD" 0 "" "AsText" "" ] }
            |> errorOrFail "AddTemplate"

        Assert.Equal(ActionNames.MyDogsbody.Startup.TemplateApi.addTemplate, actual.ActionName)
        Assert.Equal("The rule for Reference reads a part a Body template never sees.", actual.Message)
        Assert.Empty(api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier")
    )

[<Fact; Trait("Level", "Integration")>]
let ``ReorderTemplates refuses an order naming the same template twice, as an Error rather than a raise`` () =
    withApi (fun api supplierId ->
        api.AddTemplate { aTemplate supplierId with Name = "First" } |> okOrFail "AddTemplate"
        api.AddTemplate { aTemplate supplierId with Name = "Second" } |> okOrFail "AddTemplate"

        let stored = api.GetTemplatesForSupplier supplierId |> okOrFail "GetTemplatesForSupplier"
        let firstId = (stored |> List.find (fun t -> t.Name = "First")).Id

        let actual = api.ReorderTemplates supplierId [ firstId; firstId ] |> errorOrFail "ReorderTemplates"

        Assert.Equal(ActionNames.MyDogsbody.Startup.TemplateApi.reorderTemplates, actual.ActionName)
        Assert.Equal($"The new order names template '{firstId}' more than once.", actual.Message)
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

/// The pasted text has to reach the part the template actually reads. An attachment-scoped
/// template never sees BodyPart (ApplyTemplateWorkflow.partMatchesSelector), so sample text put
/// only on the body makes the panel report every field of a perfectly good PDF template as
/// unmatched - and a PDF attached to an email is the primary case this change exists for.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate runs an attachment-scoped template against the pasted sample text`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            {
                Template = { aTemplate supplierId with DocumentPart = "Attachment"; AttachmentFormat = "Pdf" }
                SampleText = "Invoice: INV-9001\nTotal: 245.00"
                SampleSubject = ""
                SampleAttachmentFilename = "invoice.pdf"
            }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.True(referenceResult.Succeeded, $"Reference failed: {referenceResult.FailureReason}")
        Assert.Equal("INV-9001", referenceResult.ParsedValue)
        let amountResult = actual.FieldResults |> List.find (fun r -> r.Field = "Amount")
        Assert.True(amountResult.Succeeded, $"Amount failed: {amountResult.FailureReason}")
        Assert.Equal("245.00", amountResult.ParsedValue)
    )

/// AttachmentName reads the filename, and the filename alone - putting the sample text on the
/// attachment must not also make it readable as a name.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate matches an AttachmentName rule against the sample filename`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            {
                Template =
                    { aTemplate supplierId with
                        Rules =
                            [ uiRule "Reference" "AttachmentName" @"(INV-\d+)" 0 "" "AsText" ""
                              uiRule "Amount" "AfterLabel" "Total:" 0 "" "AsMoney" "."
                              uiRule "Currency" "FixedValue" "AUD" 0 "" "AsText" "" ] }
                SampleText = "Total: 245.00"
                SampleSubject = ""
                SampleAttachmentFilename = "INV-9001.pdf"
            }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let referenceResult = actual.FieldResults |> List.find (fun r -> r.Field = "Reference")
        Assert.True(referenceResult.Succeeded, $"Reference failed: {referenceResult.FailureReason}")
        Assert.Equal("INV-9001", referenceResult.ParsedValue)
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

/// IssueDate and DueDate are OPTIONAL - ValidateTemplateWorkflow requires a rule only for
/// Reference, Amount and Currency. A template that declares no date rule has not failed to extract
/// a date; it was never asked to. Reporting one anyway tells the author two fields of a sound
/// template are broken, which is the same false negative the attachment-part fix closed.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate reports only the fields the template carries a rule for`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId
              SampleText = "Invoice: INV-9001\nTotal: 245.00"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        Assert.Equal<string list>([ "Reference"; "Amount"; "Currency" ], actual.FieldResults |> List.map (fun r -> r.Field))
    )

/// The other half of the rule above: a field the template DOES carry a rule for, whose rule found
/// nothing, is still reported as a failure. Dropping the fields with no rule must not turn into
/// dropping the fields that failed.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate still reports an optional date rule that found nothing`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template =
                { aTemplate supplierId with
                    Rules = validRulesUi @ [ uiRule "IssueDate" "AfterLabel" "Dated:" 0 "" "AsDate" "d MMM yyyy" ] }
              SampleText = "Invoice: INV-9001\nTotal: 245.00"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        Assert.Equal<string list>(
            [ "Reference"; "Amount"; "Currency"; "IssueDate" ],
            actual.FieldResults |> List.map (fun r -> r.Field)
        )

        let issueDateResult = actual.FieldResults |> List.find (fun r -> r.Field = "IssueDate")
        Assert.False issueDateResult.Succeeded
        Assert.Equal("No value extracted.", issueDateResult.FailureReason)
    )

/// The reason is what the panel puts in front of the author, so it is a sentence - not
/// `string error`, which renders the union as `TemplateMatchedNothing (TemplateId "test", Amount)`
/// and quotes a template id the composition root invented for the run.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate reports a rule that found nothing as a sentence, not a union dump`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId
              SampleText = "Invoice: INV-9001"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let amountResult = actual.FieldResults |> List.find (fun r -> r.Field = "Amount")
        Assert.False amountResult.Succeeded
        Assert.Equal("The rule for Amount found nothing in the sample text.", amountResult.FailureReason)
    )

/// Running the panel before anything has been pasted is the first thing a user does, and a cleared
/// MudTextField hands a bound `string` back as null - so the splitter has to take one. It reached
/// String.Replace directly, so TestTemplate raised NullReferenceException out of an API whose type
/// promises a Result, on a path the UI calls from Async.Start where neither an alert nor the log
/// would ever see it.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate reports empty sample text rather than raising`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId
              SampleText = null
              SampleSubject = null
              SampleAttachmentFilename = null }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        Assert.Equal("", actual.NormalizedText)
        Assert.Equal<string list>([ "Reference"; "Amount"; "Currency" ], actual.FieldResults |> List.map (fun r -> r.Field))

        for field in actual.FieldResults do
            Assert.False(field.Succeeded, $"{field.Field} should not have succeeded against empty sample text")
            Assert.False(String.IsNullOrWhiteSpace field.FailureReason, $"{field.Field} carried no reason")
    )

/// ParseHint.AsDate carries the rule in its own declaration - "explicit. NEVER DateTime.Parse with
/// ambient culture" - and validateDateFormat and TemplateApiMappers.toFieldFailureReason both pin
/// InvariantCulture and say why. This was the one date rendering left ambient: DateTime.ToString
/// resolves "yyyy" against CurrentCulture's CALENDAR, so 4 March 2026 prints 2569-03-04 under
/// th-TH and 1447-09-15 under ar-SA - the panel showing the author a date their template never
/// extracted, in the one screen the change exists to let them check it against.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate renders extracted dates as ISO whatever the machine's culture`` () =
    let originalCulture = Globalization.CultureInfo.CurrentCulture

    try
        Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo "th-TH"

        withApi (fun api supplierId ->
            let input: TemplateTestInputUiType =
                { Template =
                    { aTemplate supplierId with
                        Rules =
                            validRulesUi
                            @ [ uiRule "IssueDate" "AfterLabel" "Dated:" 0 "" "AsDate" "d MMM yyyy"
                                uiRule "DueDate" "DateFromField" "" 0 "IssueDate" "AsDate" "d MMM yyyy" ] }
                  SampleText = "Invoice: INV-9001\nTotal: 245.00\nDated: 4 Mar 2026"
                  SampleSubject = ""
                  SampleAttachmentFilename = "" }

            let actual = api.TestTemplate input |> okOrFail "TestTemplate"

            let issueDate = actual.FieldResults |> List.find (fun r -> r.Field = "IssueDate")
            Assert.True(issueDate.Succeeded, $"IssueDate failed: {issueDate.FailureReason}")
            Assert.Equal("2026-03-04", issueDate.ParsedValue)
            Assert.Equal("2026-03-04", issueDate.RawValue)

            // withApi's supplier carries a 30-day term, so the derived due date is 3 April - a
            // DIFFERENT date from its source, which is what keeps this asserting the rendering of a
            // derived value rather than the rendering of the issue date twice.
            let dueDate = actual.FieldResults |> List.find (fun r -> r.Field = "DueDate")
            Assert.True(dueDate.Succeeded, $"DueDate failed: {dueDate.FailureReason}")
            Assert.Equal("2026-04-03", dueDate.ParsedValue)
        )
    finally
        Globalization.CultureInfo.CurrentCulture <- originalCulture

/// requirements.md: "WHEN DateFromField is applied THE SYSTEM SHALL derive the date by adding the
/// SUPPLIER'S payment term to the date the named source field yielded", and "WHEN the invoice-
/// management platform's template is tested THE SYSTEM SHALL PROVE DateFromField produces the due
/// date its documents never state - the rule that carries extraction from 12% to 39%."
///
/// A hard-coded term of 0 makes the panel report the issue date as the due date, marked Succeeded,
/// on the one screen the change exists to let an author check extraction against. The supplier's
/// real term is reachable: the input carries its SupplierId and the factory already binds
/// loadSuppliersForTemplates for AddTemplate.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate derives a due date with the supplier's own payment term, not a placeholder`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template =
                { aTemplate supplierId with
                    Rules =
                        validRulesUi
                        @ [ uiRule "IssueDate" "AfterLabel" "Dated:" 0 "" "AsDate" "d MMM yyyy"
                            uiRule "DueDate" "DateFromField" "" 0 "IssueDate" "AsDate" "d MMM yyyy" ] }
              SampleText = "Invoice: INV-9001\nTotal: 245.00\nDated: 4 Mar 2026"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let issueDate = actual.FieldResults |> List.find (fun r -> r.Field = "IssueDate")
        Assert.True(issueDate.Succeeded, $"IssueDate failed: {issueDate.FailureReason}")
        Assert.Equal("2026-03-04", issueDate.ParsedValue)

        // withApi inserts its supplier with PaymentTermDays = 30. 4 March + 30 days = 3 April.
        let dueDate = actual.FieldResults |> List.find (fun r -> r.Field = "DueDate")
        Assert.True(dueDate.Succeeded, $"DueDate failed: {dueDate.FailureReason}")
        Assert.Equal("2026-04-03", dueDate.ParsedValue)
        Assert.Equal("2026-04-03", dueDate.RawValue)
        Assert.Equal("", dueDate.FailureReason)
    )

/// Reading the term means the supplier has to exist, so the panel now answers an unknown supplier
/// the same way AddTemplate does rather than testing against an invented one.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate reports an unknown supplier rather than testing against an invented payment term`` () =
    withApi (fun api _ ->
        let input: TemplateTestInputUiType =
            { Template = { aTemplate "9999" with SupplierId = "9999" }
              SampleText = "Invoice: INV-9001\nTotal: 245.00"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> errorOrFail "TestTemplate"

        Assert.Equal(ActionNames.MyDogsbody.Startup.TemplateApi.testTemplate, actual.ActionName)
        Assert.Equal("No supplier was found with id '9999'.", actual.Message)
        Assert.IsType<ApplicationException>(actual.InnerException) |> ignore
    )

/// An amount the rule DID find but could not read is a different sentence from one it never found,
/// and it quotes the text it choked on - that text is the whole diagnostic.
[<Fact; Trait("Level", "Integration")>]
let ``TestTemplate reports an unreadable amount as a sentence quoting the text it found`` () =
    withApi (fun api supplierId ->
        let input: TemplateTestInputUiType =
            { Template = aTemplate supplierId
              SampleText = "Invoice: INV-9001\nTotal: two hundred"
              SampleSubject = ""
              SampleAttachmentFilename = "" }

        let actual = api.TestTemplate input |> okOrFail "TestTemplate"

        let amountResult = actual.FieldResults |> List.find (fun r -> r.Field = "Amount")
        Assert.False amountResult.Succeeded
        Assert.Equal("The rule for Amount found 'two hundred', which is not a number it can read.", amountResult.FailureReason)
    )
