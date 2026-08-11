module MyDogsbody.Domain.InvoiceTemplates.ReorderTemplatesWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

let private parseTemplateIds (idStrings: string list) : Result<TemplateId list, TemplateError> =
    idStrings
    |> List.fold
        (fun accumulated idString ->
            accumulated
            |> Result.bind (fun ids ->
                TemplateId.create idString
                |> Result.mapError TemplateIdInvalid
                |> Result.map (fun id -> id :: ids)))
        (Ok [])
    |> Result.map List.rev

/// The first submitted id that names no template of this supplier's - TemplateNotFound is an
/// exact semantic fit, so this reuses it rather than adding a case.
let private ensureNoForeignTemplate (existingIds: Set<TemplateId>) (submitted: TemplateId list) : Result<unit, TemplateError> =
    match submitted |> List.tryFind (fun id -> not (existingIds.Contains id)) with
    | Some foreignId -> Error (TemplateNotFound foreignId)
    | None -> Ok ()

/// Every one of the supplier's existing templates the submitted order left out, not only the
/// first - the same reasoning MultipleSuppliersMatched carries every match rather than one.
let private ensureNothingOmitted (existingIds: Set<TemplateId>) (submitted: TemplateId list) : Result<unit, TemplateError> =
    let missing = Set.difference existingIds (Set.ofList submitted) |> Set.toList

    if List.isEmpty missing then Ok () else Error (ReorderIncomplete missing)

/// Persists a new order for a supplier's templates. Refuses an order naming a template that
/// isn't the supplier's, and refuses one that leaves an existing template out - a silent partial
/// reorder would mean the omitted template's position becomes whatever it happened to be before,
/// no longer reflecting the user's intent.
let reorderTemplates
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (reorderTemplatesDependency: ReorderTemplates)
    (supplierIdString: string)
    (templateIdStrings: string list)
    : Result<unit, TemplateError> =
    result {
        let! supplierId = SupplierId.create supplierIdString |> Result.mapError TemplateSupplierIdInvalid
        let! templateIds = parseTemplateIds templateIdStrings
        let! existingTemplates = loadTemplatesForSupplier supplierId
        let existingIds = existingTemplates |> List.map (fun template -> template.Id) |> Set.ofList
        do! ensureNoForeignTemplate existingIds templateIds
        do! ensureNothingOmitted existingIds templateIds
        return! reorderTemplatesDependency supplierId templateIds
    }
