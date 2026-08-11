module MyDogsbody.Domain.InvoiceTemplates.DeleteTemplateWorkflow

open MyDogsbody.Domain

/// Deletes an existing template. Parses the id, deletes, turns a false result (no row carried
/// that identifier) into TemplateNotFound.
let deleteTemplate (deleteTemplateDependency: DeleteTemplate) (input: string) : Result<unit, TemplateError> =
    result {
        let! id = TemplateId.create input |> Result.mapError TemplateIdInvalid
        let! deleted = deleteTemplateDependency id

        return!
            if deleted then
                Ok ()
            else
                Error (TemplateNotFound id)
    }
