module MyDogsbody.Domain.InvoiceTemplates.ListTemplatesWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

/// The sort is the workflow's, not the store's, so it is unit-tested without a database: the
/// page must show a stable order regardless of what order the dependency happened to return.
let listTemplates
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (supplierIdString: string)
    : Result<StoredTemplate list, TemplateError> =
    result {
        let! supplierId = SupplierId.create supplierIdString |> Result.mapError TemplateSupplierIdInvalid
        let! templates = loadTemplatesForSupplier supplierId
        let orderedByPosition = List.sortBy (fun template -> ValidTemplate.position template.Template)
        return templates |> orderedByPosition
    }
