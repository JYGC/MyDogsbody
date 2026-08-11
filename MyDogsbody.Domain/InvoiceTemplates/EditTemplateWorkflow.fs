module MyDogsbody.Domain.InvoiceTemplates.EditTemplateWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

let private ensureTemplateExists (stored: StoredTemplate list) (templateId: TemplateId) : Result<unit, TemplateError> =
    if stored |> List.exists (fun template -> template.Id = templateId) then
        Ok ()
    else
        Error (TemplateNotFound templateId)

/// Edits an existing template, replacing its rule set rather than merging it with what was
/// stored - editTemplate never reads the previous rules, so there is nothing to merge with.
///
/// Loads before it writes so that "no such template" is a decision made here (matching
/// EditSupplierWorkflow's ensureExists), rather than resting solely on updateTemplate's own
/// None return - though that remains the authoritative check at the write itself.
let editTemplate
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (updateTemplate: UpdateTemplate)
    (templateIdString: string)
    (input: UnvalidatedTemplate)
    : Result<StoredTemplate, TemplateError> =
    result {
        let! templateId = TemplateId.create templateIdString |> Result.mapError TemplateIdInvalid
        let! supplierId = SupplierId.create input.SupplierId |> Result.mapError TemplateSupplierIdInvalid
        let! existingTemplates = loadTemplatesForSupplier supplierId
        do! ensureTemplateExists existingTemplates templateId
        let! validated = ValidateTemplateWorkflow.validateTemplate input
        let! updated = updateTemplate templateId validated

        return!
            match updated with
            | Some stored -> Ok stored
            | None -> Error (TemplateNotFound templateId)
    }
