module MyDogsbody.Domain.InvoiceTemplates.AddTemplateWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

let private ensureSupplierExists (suppliers: StoredSupplier list) (supplierId: SupplierId) : Result<unit, TemplateError> =
    if suppliers |> List.exists (fun supplier -> supplier.Id = supplierId) then
        Ok ()
    else
        Error (TemplateSupplierNotFound supplierId)

/// One past the highest position in use, not the number of templates: nothing renumbers the
/// survivors after a delete, so gaps are the normal state and a count would hand the new template
/// a position an existing one already holds. Position decides which template is tried against an
/// invoice first, and ListTemplatesWorkflow's sort is stable, so a collision would leave both the
/// displayed order and the matching precedence decided by whatever order the store happened to
/// return - the exact outcome pulling that sort into the workflow was meant to prevent.
let private nextPosition (existingTemplates: StoredTemplate list) : int =
    match existingTemplates |> List.map (fun template -> ValidTemplate.position template.Template) with
    | [] -> 0
    | positions -> List.max positions + 1

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
        let positioned = { input with Position = nextPosition existingTemplates }
        let! validated = ValidateTemplateWorkflow.validateTemplate positioned
        return! saveTemplate validated
    }
