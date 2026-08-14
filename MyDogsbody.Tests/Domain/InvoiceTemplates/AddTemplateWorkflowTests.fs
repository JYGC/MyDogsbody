module MyDogsbody.Tests.Domain.InvoiceTemplates.AddTemplateWorkflowTests

open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private storedSupplier id : StoredSupplier =
    {
        Id = SupplierId.create id |> valueOrFail
        Name = SupplierName.create "Acme" |> valueOrFail
        PaymentTermDays = PaymentTermDays.create 30 |> valueOrFail
        Matchers = []
    }

let private validRules =
    [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
      { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
      { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]

let private unvalidatedTemplate supplierId : UnvalidatedTemplate =
    { SupplierId = supplierId
      Name = "Monthly statement"
      Part = AnyPart
      Position = 999 // deliberately wrong - addTemplate must compute the real position itself
      Rules = validRules }

/// A SaveTemplate that records what it was handed, so "the store was never reached" is
/// assertable rather than assumed.
let private recordingSave () =
    let received = ResizeArray<ValidTemplate>()

    let save: SaveTemplate =
        fun template ->
            received.Add template
            Ok { Id = TemplateId.create "99" |> valueOrFail; Template = template }

    save, received

let private loadSuppliers (suppliers: StoredSupplier list) : LoadSuppliersForTemplates = fun () -> Ok suppliers

let private loadTemplates (templates: StoredTemplate list) : LoadTemplatesForSupplier = fun _ -> Ok templates

[<Fact; Trait("Level", "Unit")>]
let ``addTemplate saves a valid template and returns every stored field`` () =
    let save, received = recordingSave ()
    let entered = unvalidatedTemplate "1"

    let actual =
        AddTemplateWorkflow.addTemplate (loadSuppliers [ storedSupplier "1" ]) (loadTemplates []) save entered

    match actual with
    | Ok stored ->
        Assert.Equal("99", TemplateId.value stored.Id)
        Assert.Equal("1", SupplierId.value (ValidTemplate.supplierId stored.Template))
        Assert.Equal("Monthly statement", TemplateName.value (ValidTemplate.name stored.Template))
        Assert.Equal<DocumentPart>(AnyPart, ValidTemplate.part stored.Template)
        Assert.Equal(3, (ValidTemplate.rules stored.Template).Length)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Single received |> ignore

/// A stored template holding a specific Position - the fixture the positioning tests turn on, so
/// it is set explicitly rather than inherited from unvalidatedTemplate's deliberately-wrong 999.
let private storedTemplateAt id position : StoredTemplate =
    { Id = TemplateId.create id |> valueOrFail
      Template =
        ValidateTemplateWorkflow.validateTemplate { unvalidatedTemplate "1" with Position = position }
        |> function
            | Ok validated -> validated
            | Error error -> failwith $"Test setup produced an invalid template: {error}" }

[<Fact; Trait("Level", "Unit")>]
let ``addTemplate positions a new template last in the supplier's existing order`` () =
    let save, received = recordingSave ()

    let actual =
        AddTemplateWorkflow.addTemplate
            (loadSuppliers [ storedSupplier "1" ])
            (loadTemplates [ storedTemplateAt "10" 0; storedTemplateAt "11" 1 ])
            save
            (unvalidatedTemplate "1")

    match actual with
    | Ok _ -> ()
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let saved = Assert.Single received
    Assert.Equal(2, ValidTemplate.position saved)

[<Fact; Trait("Level", "Unit")>]
let ``addTemplate positions a new template past the highest position in use, not past the count`` () =
    // Nothing renumbers the survivors after a delete, so gaps are the normal state: deleting the
    // first of three leaves positions 1 and 2. A count-derived position would hand the new
    // template position 2, which template "12" already holds - and since ListTemplatesWorkflow's
    // sort is stable, the tie would be broken by whatever order the store happened to return,
    // making both the displayed order and the matching precedence nondeterministic.
    let save, received = recordingSave ()

    let actual =
        AddTemplateWorkflow.addTemplate
            (loadSuppliers [ storedSupplier "1" ])
            (loadTemplates [ storedTemplateAt "11" 1; storedTemplateAt "12" 2 ])
            save
            (unvalidatedTemplate "1")

    match actual with
    | Ok _ -> ()
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let saved = Assert.Single received
    Assert.Equal(3, ValidTemplate.position saved)

[<Fact; Trait("Level", "Unit")>]
let ``addTemplate positions the first template of a supplier at zero`` () =
    let save, received = recordingSave ()

    let actual =
        AddTemplateWorkflow.addTemplate (loadSuppliers [ storedSupplier "1" ]) (loadTemplates []) save (unvalidatedTemplate "1")

    match actual with
    | Ok _ -> ()
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let saved = Assert.Single received
    Assert.Equal(0, ValidTemplate.position saved)

[<Fact; Trait("Level", "Unit")>]
let ``addTemplate refuses a supplier id that names no stored supplier, and never reaches the store`` () =
    let save, received = recordingSave ()

    let actual =
        AddTemplateWorkflow.addTemplate (loadSuppliers []) (loadTemplates []) save (unvalidatedTemplate "1")

    match actual with
    | Error (TemplateSupplierNotFound supplierId) -> Assert.Equal("1", SupplierId.value supplierId)
    | other -> Assert.Fail($"Expected Error(TemplateSupplierNotFound _), but got {other}")

    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``addTemplate propagates a validation refusal with its payload, and never reaches the store`` () =
    let save, received = recordingSave ()
    let entered = { unvalidatedTemplate "1" with Name = "" }

    let actual =
        AddTemplateWorkflow.addTemplate (loadSuppliers [ storedSupplier "1" ]) (loadTemplates []) save entered

    match actual with
    | Error (TemplateNameInvalid reason) -> Assert.Equal("Template name must not be empty.", reason)
    | other -> Assert.Fail($"Expected Error(TemplateNameInvalid _), but got {other}")

    Assert.Empty received
