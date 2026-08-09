module MyDogsbody.Domain.Suppliers.AddSupplierWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

/// Validates every submitted match rule, stopping at the first failure and naming which rule
/// failed. A partially valid supplier is never handed to the store.
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

let private validate (input: UnvalidatedSupplier) : Result<ValidSupplier, SupplierError> =
    result {
        let! name = SupplierName.create input.Name |> Result.mapError SupplierNameInvalid
        let! term = PaymentTermDays.create input.PaymentTermDays |> Result.mapError PaymentTermInvalid
        let! matchers = validateMatchers input.Matchers

        return
            {
                Name = name
                PaymentTermDays = term
                Matchers = matchers
            }
    }

/// A name that only differs by case or surrounding whitespace is still taken - SupplierName
/// already trims, so only case remains to compare here.
let private ensureNameFree
    (stored: StoredSupplier list)
    (candidate: ValidSupplier)
    : Result<ValidSupplier, SupplierError> =
    let clash =
        stored
        |> List.tryFind (fun supplier ->
            System.String.Equals(
                SupplierName.value supplier.Name,
                SupplierName.value candidate.Name,
                System.StringComparison.OrdinalIgnoreCase
            ))

    match clash with
    | Some existing -> Error (SupplierNameTaken (SupplierName.value existing.Name))
    | None -> Ok candidate

/// Adds a new supplier.
///
/// Dependencies first, input last, Result out. loadSuppliers performing a database read is
/// invisible here on purpose - this file sees a function value, which is why the whole workflow
/// tests with lambdas.
let addSupplier
    (loadSuppliers: LoadSuppliers)
    (saveSupplier: SaveSupplier)
    (input: UnvalidatedSupplier)
    : Result<StoredSupplier, SupplierError> =
    result {
        let! candidate = validate input
        let! stored = loadSuppliers ()
        let! confirmed = ensureNameFree stored candidate
        return! saveSupplier confirmed
    }
