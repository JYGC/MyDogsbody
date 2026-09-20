module MyDogsbody.Domain.InvoiceTemplates.EditTemplateWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

/// The edit needs its Position, and having found the row once there is nothing to look up a
/// second time.
let private findTheStoredTemplateRatherThanOnlyConfirmingItExists
    (stored: StoredTemplate list)
    (templateId: TemplateId)
    : Result<StoredTemplate, TemplateError> =
    match stored |> List.tryFind (fun template -> template.Id = templateId) with
    | Some template -> Ok template
    | None -> Error (TemplateNotFound templateId)

/// Replaces its rule set rather than merging it with what was stored - editTemplate never reads
/// the previous rules, so there is nothing to merge with.
///
/// Loads before it writes so that "no such template" is a decision made here (matching
/// EditSupplierWorkflow's ensureAStoredSupplierAlreadyHasTheEditedId), rather than resting solely
/// on updateTemplate's own None return - though that remains the authoritative check at the
/// write itself.
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
        let! existing = findTheStoredTemplateRatherThanOnlyConfirmingItExists existingTemplates templateId

        // addTemplate computes the position and reorderTemplates is the only workflow that changes
        // it, so an edit carries the stored one through rather than whatever the dialog round-tripped -
        // UnvalidatedTemplate is the untrusted type, and a Position arriving on it could be
        // negative or collide with a sibling, bypassing the reorder path entirely.
        let storedPositionCarriedThroughBecauseItIsNotTheCallersToSet =
            ValidTemplate.position existing.Template

        let! validated =
            ValidateTemplateWorkflow.validateTemplate
                { input with Position = storedPositionCarriedThroughBecauseItIsNotTheCallersToSet }

        let! updated = updateTemplate templateId validated

        return!
            match updated with
            | Some stored -> Ok stored
            | None -> Error (TemplateNotFound templateId)
    }
