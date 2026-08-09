module MyDogsbody.Domain.Suppliers.EditSupplierWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

let private validateMatchers
    (matchers: (MatcherKind * string) list)
    : Result<SupplierMatcher list, SupplierError> =
    let rec loop remaining acc =
        match remaining with
        | [] -> Ok (List.rev acc)
        | (kind, value) :: rest ->
            match SupplierMatcher.create kind value with
            | Ok matcher -> loop rest (matcher :: acc)
            | Error reason -> Error (MatcherInvalid reason)

    loop matchers []

let private validate (input: UnvalidatedSupplierEdit) : Result<ValidSupplierEdit, SupplierError> =
    result {
        let! id = SupplierId.create input.Id |> Result.mapError SupplierIdInvalid
        let! name = SupplierName.create input.Name |> Result.mapError SupplierNameInvalid
        let! term = PaymentTermDays.create input.PaymentTermDays |> Result.mapError PaymentTermInvalid
        let! matchers = validateMatchers input.Matchers

        return
            {
                Id = id
                Name = name
                PaymentTermDays = term
                Matchers = matchers
            }
    }

/// The row must already exist. Identified by its id and nothing else.
let private ensureExists
    (stored: StoredSupplier list)
    (edit: ValidSupplierEdit)
    : Result<ValidSupplierEdit, SupplierError> =
    if stored |> List.exists (fun supplier -> supplier.Id = edit.Id) then
        Ok edit
    else
        Error (SupplierNotFound edit.Id)

/// A clash against another supplier's name - the row's own current name is excluded, so leaving
/// the name unchanged never reports itself as taken.
let private ensureNameFree
    (stored: StoredSupplier list)
    (edit: ValidSupplierEdit)
    : Result<ValidSupplierEdit, SupplierError> =
    let clash =
        stored
        |> List.tryFind (fun supplier ->
            supplier.Id <> edit.Id
            && System.String.Equals(
                SupplierName.value supplier.Name,
                SupplierName.value edit.Name,
                System.StringComparison.OrdinalIgnoreCase
            ))

    match clash with
    | Some existing -> Error (SupplierNameTaken (SupplierName.value existing.Name))
    | None -> Ok edit

/// Edits an existing supplier, replacing its match rules rather than merging with them.
///
/// Loads before it writes so that both "no such supplier" and "name already taken" are decisions
/// made here, in a function a test can drive with two lambdas, rather than buried in a query.
let editSupplier
    (loadSuppliers: LoadSuppliers)
    (updateSupplier: UpdateSupplier)
    (input: UnvalidatedSupplierEdit)
    : Result<StoredSupplier, SupplierError> =
    result {
        let! validEdit = validate input
        let! storedSuppliers = loadSuppliers ()
        let! confirmedExists = ensureExists storedSuppliers validEdit
        let! confirmedFree = ensureNameFree storedSuppliers confirmedExists
        let! updated = updateSupplier confirmedFree

        return!
            match updated with
            | Some storedSupplier -> Ok storedSupplier
            | None -> Error (SupplierNotFound confirmedFree.Id)
    }
