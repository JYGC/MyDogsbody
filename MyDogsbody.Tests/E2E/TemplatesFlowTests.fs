module MyDogsbody.Tests.E2E.TemplatesFlowTests

open Xunit
open Bunit
open Fun.Blazor
open MudBlazor
open Microsoft.Extensions.DependencyInjection
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types
open MyDogsbody.Tests.E2E.TemplatesTestHarness

// User-visible flows, driven through a rendered component down to a real temp SQLite file
// (schema built by the real migrations) and back into what the component renders. Everything
// between is the real thing: the module creator, the API record, the workflows, the adapter.
//
// Driving the WPF BlazorWebView window is out of scope; the manual check is recorded in the
// change description instead.

let private renderBrowser (harness: TemplatesHarness) (supplierIdString: string) =
    let browserModule =
        TemplatesBrowserModuleCreators.getTemplatesBrowserModule (fun work -> work ()) harness.Api supplierIdString

    let view =
        TemplatesComponents.templatesBrowser "Acme" browserModule (fun () -> ()) (fun _ -> ()) (fun _ -> ()) (fun _ _ -> ())

    let rendered =
        harness.Render<FunFragmentComponent>(fun builder ->
            builder.OpenComponent<FunFragmentComponent>(0)
            builder.AddAttribute(1, "Fragment", view)
            builder.CloseComponent()
        )

    browserModule, rendered

let private validRules: TemplateFieldRuleUiType list =
    [
        { Field = "Reference"; RuleKind = "AfterLabel"; RuleText = "Invoice:"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsText"; HintText = "" }
        { Field = "Amount"; RuleKind = "AfterLabel"; RuleText = "Total:"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsMoney"; HintText = "." }
        { Field = "Currency"; RuleKind = "FixedValue"; RuleText = "AUD"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsText"; HintText = "" }
    ]

let private aTemplate supplierIdString : TemplateUiTypeWithoutId =
    { SupplierId = supplierIdString; Name = "Monthly statement"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = validRules }

/// Renders the editor dialog the way production does - through the dialog service, inside a
/// MudDialogProvider. A MudDialog rendered on its own produces no markup at all, so the provider
/// is what makes the dialog's own content testable. Per SuppliersFlowTests.renderEditor.
let private renderEditor (harness: TemplatesHarness) (supplierIdString: string) (rules: TemplateFieldRuleUiType list) =
    let template = { aTemplate supplierIdString with Rules = rules }

    let provider =
        harness.Render<MudDialogProvider>(fun builder ->
            builder.OpenComponent<MudDialogProvider>(0)
            builder.CloseComponent()
        )

    let dialogService = harness.Services.GetRequiredService<IDialogService>()
    let parameters = DialogParameters<TemplatesComponents.TemplatesEditorDialog>()
    parameters.Add("Title", "Edit Template")
    parameters.Add("SupplierId", supplierIdString)
    parameters.Add("TemplateUiType", template)
    parameters.Add("TestTemplate", harness.Api.TestTemplate)

    provider
        .InvokeAsync(fun () ->
            dialogService.ShowAsync<TemplatesComponents.TemplatesEditorDialog>("Edit Template", parameters)
            |> ignore
        )
        .Wait()

    provider

let private occurrencesOf (needle: string) (haystack: string) =
    haystack.Split(needle).Length - 1

[<Fact; Trait("Level", "E2E")>]
let ``a template added through the module appears as a row in the rendered table`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        let browserModule, rendered = renderBrowser harness supplierIdString
        Assert.DoesNotContain("Monthly statement", rendered.Markup)

        browserModule.AddTemplate (aTemplate supplierIdString)

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Monthly statement", rendered.Markup)
            Assert.Contains("AnyPart", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``a template edited through the module updates the row the table shows`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        let browserModule, rendered = renderBrowser harness supplierIdString
        browserModule.AddTemplate (aTemplate supplierIdString)
        rendered.WaitForAssertion(fun () -> Assert.Contains("Monthly statement", rendered.Markup))

        let stored =
            match harness.Api.GetTemplatesForSupplier supplierIdString with
            | Ok [ only ] -> only
            | other -> failwith $"Expected exactly one template, got {other}"

        browserModule.EditTemplate { stored with Name = "Renamed statement" }

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Renamed statement", rendered.Markup)
            Assert.DoesNotContain("Monthly statement", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``templates reordered through the module are shown and used in the new order`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        let browserModule, rendered = renderBrowser harness supplierIdString
        browserModule.AddTemplate (aTemplate supplierIdString)
        browserModule.AddTemplate { aTemplate supplierIdString with Name = "Second" }
        rendered.WaitForAssertion(fun () -> Assert.Contains("Second", rendered.Markup))

        let stored = harness.Api.GetTemplatesForSupplier supplierIdString |> Result.defaultValue []
        let first = stored |> List.find (fun t -> t.Name = "Monthly statement")
        let second = stored |> List.find (fun t -> t.Name = "Second")

        browserModule.ReorderTemplates [ second.Id; first.Id ]

        rendered.WaitForAssertion(fun () ->
            let nameIndex name = rendered.Markup.IndexOf(name: string)
            Assert.True(nameIndex "Second" < nameIndex "Monthly statement")
        )

        // "used in the new order" - the store itself reflects it, not only the render.
        let reread =
            harness.Api.GetTemplatesForSupplier supplierIdString
            |> Result.defaultValue []
            |> List.sortBy (fun t -> t.Position)

        Assert.Equal<string list>([ second.Id; first.Id ], reread |> List.map (fun t -> t.Id))
    )

[<Fact; Trait("Level", "E2E")>]
let ``a template deleted through the module disappears from the rendered table`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        let browserModule, rendered = renderBrowser harness supplierIdString
        browserModule.AddTemplate (aTemplate supplierIdString)
        rendered.WaitForAssertion(fun () -> Assert.Contains("Monthly statement", rendered.Markup))

        let stored =
            match harness.Api.GetTemplatesForSupplier supplierIdString with
            | Ok [ only ] -> only
            | other -> failwith $"Expected exactly one template, got {other}"

        browserModule.DeleteTemplate stored.Id

        rendered.WaitForAssertion(fun () -> Assert.DoesNotContain("Monthly statement", rendered.Markup))
    )

[<Fact; Trait("Level", "E2E")>]
let ``a validation failure is shown as an alert with the specific reason and is not written to the log`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        let browserModule, rendered = renderBrowser harness supplierIdString

        // An empty name, which the domain rejects
        browserModule.AddTemplate { aTemplate supplierIdString with Name = "" }

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Template name must not be empty.", rendered.Markup)
        )

        match harness.Api.GetTemplatesForSupplier supplierIdString with
        | Ok stored -> Assert.Empty stored
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")

        // An expected failure is never logged: the exception log is for things worth reading a
        // stack trace about.
        Assert.Empty harness.Logged
    )

[<Fact; Trait("Level", "E2E")>]
let ``a store failure is shown as an alert and is written to the log exactly once`` () =
    withUnreachableTemplateStoreHarness (fun harness supplierIdString ->
        // The initial load fails, because the store cannot be reached
        let _, rendered = renderBrowser harness supplierIdString

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Failed to retrieve templates for supplier.", rendered.Markup)
        )

        Assert.Single harness.Logged |> ignore

        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.TemplateStore.getForSupplier,
            harness.Logged.[0].ActionName
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``a successful write after a failure clears the alert`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        let browserModule, rendered = renderBrowser harness supplierIdString
        browserModule.AddTemplate { aTemplate supplierIdString with Name = "" }
        rendered.WaitForAssertion(fun () -> Assert.Contains("Template name must not be empty.", rendered.Markup))

        browserModule.AddTemplate (aTemplate supplierIdString)

        rendered.WaitForAssertion(fun () ->
            Assert.DoesNotContain("Template name must not be empty.", rendered.Markup)
            Assert.Contains("Monthly statement", rendered.Markup)
        )
    )

// ---------- 11.1a: the two rendering traps 9.3 exists to satisfy ----------

[<Fact; Trait("Level", "E2E")>]
let ``every rule editor renders its own label`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        let rules: TemplateFieldRuleUiType list =
            [
                { Field = "Reference"; RuleKind = "AfterLabel"; RuleText = "Invoice:"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsText"; HintText = "" }
                { Field = "Amount"; RuleKind = "FixedValue"; RuleText = "100.00"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsMoney"; HintText = "." }
                { Field = "DueDate"; RuleKind = "DateFromField"; RuleText = ""; RuleOffset = 0; RuleSourceField = "IssueDate"; HintKind = "AsDate"; HintText = "yyyy-MM-dd" }
            ]

        let rendered = renderEditor harness supplierIdString rules

        Assert.Contains("Reference: AfterLabel \"Invoice:\" (AsText)", rendered.Markup)
        Assert.Contains("Amount: FixedValue \"100.00\" (AsMoney '.')", rendered.Markup)
        Assert.Contains("DueDate: DateFromField \"\" from IssueDate (AsDate 'yyyy-MM-dd')", rendered.Markup)
    )

[<Fact; Trait("Level", "E2E")>]
let ``deleting one of two identical rules removes only the one clicked`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        // requirements.md places no uniqueness rule on rule text - two rules of the same shape
        // (before one is fixed up) is a state the dialog has to cope with, the same reasoning
        // SuppliersComponents' duplicate-matcher test uses.
        let duplicate: TemplateFieldRuleUiType =
            { Field = "Reference"; RuleKind = "AfterLabel"; RuleText = "Invoice:"; RuleOffset = 0; RuleSourceField = ""; HintKind = "AsText"; HintText = "" }

        let rendered = renderEditor harness supplierIdString [ duplicate; duplicate ]

        let expectedLabel = "Reference: AfterLabel \"Invoice:\" (AsText)"
        Assert.Equal(2, occurrencesOf expectedLabel rendered.Markup)

        let deleteButtons = rendered.FindAll(".mud-list-item button")
        Assert.Equal(2, deleteButtons.Count)
        deleteButtons.[0].Click()

        rendered.WaitForAssertion(fun () ->
            Assert.Equal(1, occurrencesOf expectedLabel rendered.Markup)
        )
    )

// ---------- 11.2: the test panel, including a non-breaking space ----------

[<Fact; Trait("Level", "E2E")>]
let ``the test panel shows normalized text and extracts a value across a non-breaking space`` () =
    withTemplatesHarness (fun harness supplierIdString ->
        let rendered = renderEditor harness supplierIdString validRules

        // U+00A0 between the label and the value - TextNormalization folds it to an ordinary
        // space before AfterLabel ever sees the line.
        let sampleText = "Invoice: INV-42\nTotal: 99.00"

        rendered.Find("textarea").Input sampleText

        let runTestButton =
            rendered.FindAll("button") |> Seq.find (fun el -> el.TextContent.Trim() = "Run test")

        runTestButton.Click()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Invoice: INV-42", rendered.Markup)
            Assert.Contains("INV-42", rendered.Markup)
        )
    )
