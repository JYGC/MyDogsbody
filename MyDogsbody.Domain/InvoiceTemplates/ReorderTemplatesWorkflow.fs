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

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ReorderTemplatesWorkflow.fs: ensureTheSubmittedOrderDoesNotNameTheSameTemplateTwice
let private ensureTheSubmittedOrderDoesNotNameTheSameTemplateTwice
    (submitted: TemplateId list)
    : Result<unit, TemplateError> =
    match submitted |> List.countBy id |> List.tryFind (fun (_, count) -> count > 1) with
    | Some (duplicate, _) -> Error (ReorderDuplicate duplicate)
    | None -> Ok ()

/// TemplateNotFound is an exact semantic fit, so this reuses it rather than adding a case.
let private ensureEverySubmittedIdNamesATemplateOfThisSupplierReportingTheFirstThatDoesNot
    (existingIds: Set<TemplateId>)
    (submitted: TemplateId list)
    : Result<unit, TemplateError> =
    match submitted |> List.tryFind (fun id -> not (existingIds.Contains id)) with
    | Some foreignId -> Error (TemplateNotFound foreignId)
    | None -> Ok ()

/// The same reasoning as MultipleSuppliersMatched carrying every match rather than one.
let private ensureNoneOfTheSuppliersExistingTemplatesWasLeftOutReportingEveryOneLeftOutNotOnlyTheFirst
    (existingIds: Set<TemplateId>)
    (submitted: TemplateId list)
    : Result<unit, TemplateError> =
    let missing = Set.difference existingIds (Set.ofList submitted) |> Set.toList

    if List.isEmpty missing then Ok () else Error (ReorderIncomplete missing)

/// A silent partial reorder would mean the omitted template's position becomes whatever it
/// happened to be before, no longer reflecting the user's intent.
let reorderTemplates
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (reorderTemplatesDependency: ReorderTemplates)
    (supplierIdString: string)
    (templateIdStrings: string list)
    : Result<unit, TemplateError> =
    result {
        let! supplierId = SupplierId.create supplierIdString |> Result.mapError TemplateSupplierIdInvalid
        let! templateIds = parseTemplateIds templateIdStrings
        do! ensureTheSubmittedOrderDoesNotNameTheSameTemplateTwice templateIds
        let! existingTemplates = loadTemplatesForSupplier supplierId
        let existingIds = existingTemplates |> List.map (fun template -> template.Id) |> Set.ofList
        do! ensureEverySubmittedIdNamesATemplateOfThisSupplierReportingTheFirstThatDoesNot existingIds templateIds
        do!
            ensureNoneOfTheSuppliersExistingTemplatesWasLeftOutReportingEveryOneLeftOutNotOnlyTheFirst
                existingIds
                templateIds
        return! reorderTemplatesDependency supplierId templateIds
    }
