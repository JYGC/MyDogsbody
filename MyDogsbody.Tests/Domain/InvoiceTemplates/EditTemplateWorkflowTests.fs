module MyDogsbody.Tests.Domain.InvoiceTemplates.EditTemplateWorkflowTests

open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private validRules =
    [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
      { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
      { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]

let private unvalidatedTemplate supplierId rules : UnvalidatedTemplate =
    { SupplierId = supplierId
      Name = "Monthly statement"
      Part = AnyPart
      Position = 0
      Rules = rules }

let private storedTemplateWith id supplierId rules : StoredTemplate =
    {
        Id = TemplateId.create id |> valueOrFail
        Template =
            ValidateTemplateWorkflow.validateTemplate (unvalidatedTemplate supplierId rules)
            |> function
                | Ok validated -> validated
                | Error error -> failwith $"Test setup produced an invalid template: {error}"
    }

/// An UpdateTemplate that records what it was handed, so "the store was never reached" is
/// assertable rather than assumed.
let private recordingUpdate (existing: StoredTemplate list) () =
    let received = ResizeArray<TemplateId * ValidTemplate>()

    let update: UpdateTemplate =
        fun templateId template ->
            received.Add(templateId, template)

            if existing |> List.exists (fun t -> t.Id = templateId) then
                Ok (Some { Id = templateId; Template = template })
            else
                Ok None

    update, received

let private loadTemplates (templates: StoredTemplate list) : LoadTemplatesForSupplier = fun _ -> Ok templates

[<Fact; Trait("Level", "Unit")>]
let ``editTemplate updates an existing template and returns every stored field`` () =
    let existing = storedTemplateWith "10" "1" validRules
    let update, received = recordingUpdate [ existing ] ()
    let edited = unvalidatedTemplate "1" validRules

    let actual = EditTemplateWorkflow.editTemplate (loadTemplates [ existing ]) update "10" edited

    match actual with
    | Ok stored ->
        Assert.Equal("10", TemplateId.value stored.Id)
        Assert.Equal("Monthly statement", TemplateName.value (ValidTemplate.name stored.Template))
        Assert.Equal(3, (ValidTemplate.rules stored.Template).Length)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``editTemplate replaces the rule set rather than merging it with what was stored`` () =
    // The stored template carries an extra IssueDate rule the edit does not resubmit. If
    // editTemplate merged instead of replacing, that rule would survive the edit; it must not.
    let ruleSetWithIssueDate =
        validRules @ [ { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" } ]
    let existing = storedTemplateWith "10" "1" ruleSetWithIssueDate
    let update, received = recordingUpdate [ existing ] ()
    let edited = { unvalidatedTemplate "1" validRules with Name = "Different rules" }

    let actual = EditTemplateWorkflow.editTemplate (loadTemplates [ existing ]) update "10" edited

    match actual with
    | Ok stored ->
        Assert.Equal<TargetField list>(
            [ Reference; Amount; Currency ],
            (ValidTemplate.rules stored.Template) |> List.map (fun r -> r.Field)
        )
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let (_, savedTemplate) = Assert.Single received
    Assert.Equal(3, (ValidTemplate.rules savedTemplate).Length)

[<Fact; Trait("Level", "Unit")>]
let ``editTemplate refuses an id naming no stored template with TemplateNotFound, and never reaches the store`` () =
    let update, received = recordingUpdate [] ()
    let edited = unvalidatedTemplate "1" validRules

    let actual = EditTemplateWorkflow.editTemplate (loadTemplates []) update "999" edited

    match actual with
    | Error (TemplateNotFound templateId) -> Assert.Equal("999", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(TemplateNotFound _), but got {other}")

    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``editTemplate propagates a validation refusal with its payload, and never reaches the store`` () =
    let existing = storedTemplateWith "10" "1" validRules
    let update, received = recordingUpdate [ existing ] ()
    let edited = { unvalidatedTemplate "1" validRules with Name = "" }

    let actual = EditTemplateWorkflow.editTemplate (loadTemplates [ existing ]) update "10" edited

    match actual with
    | Error (TemplateNameInvalid reason) -> Assert.Equal("Template name must not be empty.", reason)
    | other -> Assert.Fail($"Expected Error(TemplateNameInvalid _), but got {other}")

    Assert.Empty received

/// A stored template holding a specific Position - what the two tests below turn on.
let private storedTemplateAt id supplierId position : StoredTemplate =
    { Id = TemplateId.create id |> valueOrFail
      Template =
        ValidateTemplateWorkflow.validateTemplate { unvalidatedTemplate supplierId validRules with Position = position }
        |> function
            | Ok validated -> validated
            | Error error -> failwith $"Test setup produced an invalid template: {error}" }

[<Fact; Trait("Level", "Unit")>]
let ``editTemplate keeps the stored position rather than the one the caller submitted`` () =
    // Position is not the caller's to set: addTemplate computes it and reorderTemplates is the only
    // workflow that changes it. UnvalidatedTemplate is the untrusted type, so an edit trusting
    // input.Position would let any int through - here 999, which no sibling holds and no reorder
    // authorised.
    let existing = storedTemplateAt "10" "1" 2
    let update, received = recordingUpdate [ existing ] ()
    let edited = { unvalidatedTemplate "1" validRules with Position = 999 }

    let actual = EditTemplateWorkflow.editTemplate (loadTemplates [ existing ]) update "10" edited

    match actual with
    | Ok stored -> Assert.Equal(2, ValidTemplate.position stored.Template)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let (_, savedTemplate) = Assert.Single received
    Assert.Equal(2, ValidTemplate.position savedTemplate)

[<Fact; Trait("Level", "Unit")>]
let ``editTemplate keeps the edited template's own position, not a sibling's`` () =
    // Two templates, and the one being edited is not the first in the loaded list - so a lookup
    // that grabbed the wrong row, or the head of the list, would show up here.
    let first = storedTemplateAt "10" "1" 0
    let second = storedTemplateAt "11" "1" 1
    let update, received = recordingUpdate [ first; second ] ()
    let edited = { unvalidatedTemplate "1" validRules with Position = -7 }

    let actual = EditTemplateWorkflow.editTemplate (loadTemplates [ first; second ]) update "11" edited

    match actual with
    | Ok stored -> Assert.Equal(1, ValidTemplate.position stored.Template)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let (_, savedTemplate) = Assert.Single received
    Assert.Equal(1, ValidTemplate.position savedTemplate)
