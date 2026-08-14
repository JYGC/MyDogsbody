module MyDogsbody.Tests.Domain.InvoiceTemplates.ReorderTemplatesWorkflowTests

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

let private storedTemplateAt id : StoredTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = "1"; Name = $"Template {id}"; Part = AnyPart; Position = 0; Rules = validRules }

    {
        Id = TemplateId.create id |> valueOrFail
        Template =
            ValidateTemplateWorkflow.validateTemplate unvalidated
            |> function
                | Ok validated -> validated
                | Error error -> failwith $"Test setup produced an invalid template: {error}"
    }

let private existingTemplates = [ storedTemplateAt "1"; storedTemplateAt "2"; storedTemplateAt "3" ]
let private loadTemplates: LoadTemplatesForSupplier = fun _ -> Ok existingTemplates

/// A ReorderTemplates that records what it was handed, so "the store was never reached" is
/// assertable rather than assumed.
let private recordingReorder () =
    let received = ResizeArray<SupplierId * TemplateId list>()

    let reorder: ReorderTemplates =
        fun supplierId templateIds ->
            received.Add(supplierId, templateIds)
            Ok ()

    reorder, received

[<Fact; Trait("Level", "Unit")>]
let ``reorderTemplates persists the new order`` () =
    let reorder, received = recordingReorder ()

    let actual = ReorderTemplatesWorkflow.reorderTemplates loadTemplates reorder "1" [ "3"; "1"; "2" ]

    Assert.Equal(Ok (), actual)
    let (_, savedOrder) = Assert.Single received
    Assert.Equal<string list>([ "3"; "1"; "2" ], savedOrder |> List.map TemplateId.value)

[<Fact; Trait("Level", "Unit")>]
let ``reorderTemplates refuses an order naming a template that does not belong to the supplier, and never reaches the store`` () =
    let reorder, received = recordingReorder ()

    let actual = ReorderTemplatesWorkflow.reorderTemplates loadTemplates reorder "1" [ "1"; "2"; "999" ]

    match actual with
    | Error (TemplateNotFound templateId) -> Assert.Equal("999", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(TemplateNotFound _), but got {other}")

    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``reorderTemplates refuses an order that omits one of the supplier's existing templates, and never reaches the store`` () =
    let reorder, received = recordingReorder ()

    let actual = ReorderTemplatesWorkflow.reorderTemplates loadTemplates reorder "1" [ "1"; "2" ]

    match actual with
    | Error (ReorderIncomplete missing) -> Assert.Equal<string list>([ "3" ], missing |> List.map TemplateId.value)
    | other -> Assert.Fail($"Expected Error(ReorderIncomplete _), but got {other}")

    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``reorderTemplates refuses an order naming the same template twice, and never reaches the store`` () =
    // Neither of the other two checks can see a duplicate: Set.ofList collapses it, so the
    // omission check's difference stays empty, and the duplicate is one of the supplier's own
    // templates so it is not foreign either. Left unchecked, [1;1;2;3] writes four positions for
    // three templates - template 1 at 0 and again at 1, 2 and 3 at 2 and 3 - leaving nothing at
    // position 0 and silently reshuffling which template an invoice is matched against first.
    let reorder, received = recordingReorder ()

    let actual = ReorderTemplatesWorkflow.reorderTemplates loadTemplates reorder "1" [ "1"; "1"; "2"; "3" ]

    match actual with
    | Error (ReorderDuplicate templateId) -> Assert.Equal("1", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(ReorderDuplicate _), but got {other}")

    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``reorderTemplates refuses a duplicate before it loads the supplier's templates`` () =
    // A duplicate is a property of the submitted list alone, so it is decided ahead of the two
    // checks that need the store - the load never happens.
    let reorder, received = recordingReorder ()
    let loadCalls = ResizeArray<SupplierId>()

    let recordingLoad: LoadTemplatesForSupplier =
        fun supplierId ->
            loadCalls.Add supplierId
            Ok existingTemplates

    let actual = ReorderTemplatesWorkflow.reorderTemplates recordingLoad reorder "1" [ "2"; "2" ]

    match actual with
    | Error (ReorderDuplicate templateId) -> Assert.Equal("2", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(ReorderDuplicate _), but got {other}")

    Assert.Empty loadCalls
    Assert.Empty received
