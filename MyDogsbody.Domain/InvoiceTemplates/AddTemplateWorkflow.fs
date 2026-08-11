module MyDogsbody.Domain.InvoiceTemplates.AddTemplateWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

let private ensureSupplierExists (suppliers: StoredSupplier list) (supplierId: SupplierId) : Result<unit, TemplateError> =
    if suppliers |> List.exists (fun supplier -> supplier.Id = supplierId) then
        Ok ()
    else
        Error (TemplateSupplierNotFound supplierId)

/// Adds a new template, positioned last in its supplier's existing order regardless of what
/// Position the caller supplied - a new template's place is computed here, not typed.
///
/// Dependencies first, input last, Result out. Existence is confirmed before validation, matching
/// design.md's sequence diagram: a supplier that does not exist is reported as
/// TemplateSupplierNotFound rather than as a validation refusal on the template's own fields.
let addTemplate
    (loadSuppliersForTemplates: LoadSuppliersForTemplates)
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (saveTemplate: SaveTemplate)
    (input: UnvalidatedTemplate)
    : Result<StoredTemplate, TemplateError> =
    result {
        let! supplierId = SupplierId.create input.SupplierId |> Result.mapError TemplateSupplierIdInvalid
        let! suppliers = loadSuppliersForTemplates ()
        do! ensureSupplierExists suppliers supplierId
        let! existingTemplates = loadTemplatesForSupplier supplierId
        let positioned = { input with Position = existingTemplates.Length }
        let! validated = ValidateTemplateWorkflow.validateTemplate positioned
        return! saveTemplate validated
    }
