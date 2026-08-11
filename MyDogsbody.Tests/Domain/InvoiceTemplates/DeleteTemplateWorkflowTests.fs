module MyDogsbody.Tests.Domain.InvoiceTemplates.DeleteTemplateWorkflowTests

open Xunit
open MyDogsbody.Domain.InvoiceTemplates

let private recordingDelete (result: Result<bool, TemplateError>) =
    let received = ResizeArray<TemplateId>()

    let delete: DeleteTemplate =
        fun templateId ->
            received.Add templateId
            result

    delete, received

[<Fact; Trait("Level", "Unit")>]
let ``deleteTemplate deletes an existing template`` () =
    let delete, received = recordingDelete (Ok true)

    let actual = DeleteTemplateWorkflow.deleteTemplate delete "10"

    Assert.Equal(Ok (), actual)
    let deletedId = Assert.Single received
    Assert.Equal("10", TemplateId.value deletedId)

[<Fact; Trait("Level", "Unit")>]
let ``deleteTemplate reports TemplateNotFound when the store found no matching row`` () =
    let delete, received = recordingDelete (Ok false)

    let actual = DeleteTemplateWorkflow.deleteTemplate delete "10"

    match actual with
    | Error (TemplateNotFound templateId) -> Assert.Equal("10", TemplateId.value templateId)
    | other -> Assert.Fail($"Expected Error(TemplateNotFound _), but got {other}")

    Assert.Single received |> ignore

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``deleteTemplate refuses an unusable id and never reaches the store`` (entered: string) =
    let delete, received = recordingDelete (Ok true)

    let actual = DeleteTemplateWorkflow.deleteTemplate delete entered

    match actual with
    | Error (TemplateIdInvalid reason) -> Assert.Equal("Template id must not be empty.", reason)
    | other -> Assert.Fail($"Expected Error(TemplateIdInvalid _), but got {other}")

    Assert.Empty received
