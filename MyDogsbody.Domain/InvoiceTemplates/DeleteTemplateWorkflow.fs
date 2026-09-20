module MyDogsbody.Domain.InvoiceTemplates.DeleteTemplateWorkflow

open MyDogsbody.Domain

let deleteTemplate (deleteTemplateDependency: DeleteTemplate) (input: string) : Result<unit, TemplateError> =
    result {
        let! id = TemplateId.create input |> Result.mapError TemplateIdInvalid
        let! aRowCarryingThatIdentifierWasDeleted = deleteTemplateDependency id

        return!
            if aRowCarryingThatIdentifierWasDeleted then
                Ok ()
            else
                Error (TemplateNotFound id)
    }
