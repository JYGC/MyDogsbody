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

/// A submitted order naming the same template twice. Neither check below can see one: Set.ofList
/// collapses the repeat, so ensureNothingOmitted's difference stays empty, and the duplicate is
/// one of the supplier's own templates so ensureNoForeignTemplate is satisfied too. design.md
/// specifies reorder as one UPDATE ... SET Position per template, so [1;1;2;3] would write four
/// positions for three templates - template 1 at 0 and again at 1, then 2 and 3 at 2 and 3 -
/// leaving nothing at position 0 and silently reshuffling which template an invoice is matched
/// against first, while reporting success.
///
/// A property of the submitted list alone, so it is decided before the store is read.
let private ensureNoDuplicate (submitted: TemplateId list) : Result<unit, TemplateError> =
    match submitted |> List.countBy id |> List.tryFind (fun (_, count) -> count > 1) with
    | Some (duplicate, _) -> Error (ReorderDuplicate duplicate)
    | None -> Ok ()

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

/// Persists a new order for a supplier's templates. Refuses an order naming the same template
/// twice, one naming a template that isn't the supplier's, and one that leaves an existing
/// template out - a silent partial reorder would mean the omitted template's position becomes
/// whatever it happened to be before, no longer reflecting the user's intent.
let reorderTemplates
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (reorderTemplatesDependency: ReorderTemplates)
    (supplierIdString: string)
    (templateIdStrings: string list)
    : Result<unit, TemplateError> =
    result {
        let! supplierId = SupplierId.create supplierIdString |> Result.mapError TemplateSupplierIdInvalid
        let! templateIds = parseTemplateIds templateIdStrings
        do! ensureNoDuplicate templateIds
        let! existingTemplates = loadTemplatesForSupplier supplierId
        let existingIds = existingTemplates |> List.map (fun template -> template.Id) |> Set.ofList
        do! ensureNoForeignTemplate existingIds templateIds
        do! ensureNothingOmitted existingIds templateIds
        return! reorderTemplatesDependency supplierId templateIds
    }
