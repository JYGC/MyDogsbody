module MyDogsbody.Domain.InvoiceTemplates.ListTemplatesWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

/// Lists a supplier's templates, ordered by Position.
///
/// The sort is the workflow's, not the store's, so it is unit-tested without a database: the
/// page must show a stable order regardless of what order the dependency happened to return.
let listTemplates
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (supplierIdString: string)
    : Result<StoredTemplate list, TemplateError> =
    result {
        let! supplierId = SupplierId.create supplierIdString |> Result.mapError TemplateSupplierIdInvalid
        let! templates = loadTemplatesForSupplier supplierId
        return templates |> List.sortBy (fun template -> ValidTemplate.position template.Template)
    }
