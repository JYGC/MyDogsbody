module MyDogsbody.Tests.Domain.InvoiceTemplates.ListTemplatesWorkflowTests

open Xunit
open MyDogsbody.Domain.InvoiceTemplates

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private validRules =
    [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
      { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
      { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]

let private storedTemplateAt id position : StoredTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = "1"; Name = $"Template {id}"; Part = AnyPart; Position = position; Rules = validRules }

    {
        Id = TemplateId.create id |> valueOrFail
        Template =
            ValidateTemplateWorkflow.validateTemplate unvalidated
            |> function
                | Ok validated -> validated
                | Error error -> failwith $"Test setup produced an invalid template: {error}"
    }

[<Fact; Trait("Level", "Unit")>]
let ``listTemplates returns the supplier's templates ordered by Position regardless of the order the dependency returned`` () =
    let outOfOrder = [ storedTemplateAt "3" 2; storedTemplateAt "1" 0; storedTemplateAt "2" 1 ]
    let loadTemplatesForSupplier: LoadTemplatesForSupplier = fun _ -> Ok outOfOrder

    let actual = ListTemplatesWorkflow.listTemplates loadTemplatesForSupplier "1"

    match actual with
    | Ok templates ->
        Assert.Equal<string list>(
            [ "1"; "2"; "3" ],
            templates |> List.map (fun t -> TemplateId.value t.Id)
        )
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``listTemplates returns an empty list rather than an error when a supplier has no templates`` () =
    let loadTemplatesForSupplier: LoadTemplatesForSupplier = fun _ -> Ok []

    let actual = ListTemplatesWorkflow.listTemplates loadTemplatesForSupplier "1"

    Assert.Equal(Ok [], actual)

[<Fact; Trait("Level", "Unit")>]
let ``listTemplates refuses an unusable supplier id`` () =
    let loadTemplatesForSupplier: LoadTemplatesForSupplier = fun _ -> Ok []

    let actual = ListTemplatesWorkflow.listTemplates loadTemplatesForSupplier ""

    match actual with
    | Error (TemplateSupplierIdInvalid reason) -> Assert.Equal("Supplier id must not be empty.", reason)
    | other -> Assert.Fail($"Expected Error(TemplateSupplierIdInvalid _), but got {other}")
