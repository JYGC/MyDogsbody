module MyDogsbody.Domain.Suppliers.AddSupplierWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

let private validateEveryMatchRuleStoppingAtTheFirstFailure
    (matchers: (MatcherKind * string) list)
    : Result<SupplierMatcher list, SupplierError> =
    let rec loop remaining accumulatedMatchers =
        match remaining with
        | [] -> Ok (List.rev accumulatedMatchers)
        | (kind, value) :: rest ->
            match SupplierMatcher.create kind value with
            | Ok matcher -> loop rest (matcher :: accumulatedMatchers)
            | Error reason -> Error (MatcherInvalid reason)

    loop matchers []

let private validate (input: UnvalidatedSupplier) : Result<ValidSupplier, SupplierError> =
    result {
        let! name = SupplierName.create input.Name |> Result.mapError SupplierNameInvalid
        let! term = PaymentTermDays.create input.PaymentTermDays |> Result.mapError PaymentTermInvalid
        let! matchers = validateEveryMatchRuleStoppingAtTheFirstFailure input.Matchers

        return
            {
                Name = name
                PaymentTermDays = term
                Matchers = matchers
            }
    }

/// SupplierName already trims, so only case remains to compare here.
let private ensureNoStoredSupplierHasTheSameNameIgnoringCase
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

/// loadSuppliers performing a database read is invisible here on purpose - this file sees a
/// function value, which is why the whole workflow tests with lambdas.
let addSupplier
    (loadSuppliers: LoadSuppliers)
    (saveSupplier: SaveSupplier)
    (input: UnvalidatedSupplier)
    : Result<StoredSupplier, SupplierError> =
    result {
        let! candidate = validate input
        let! stored = loadSuppliers ()
        let! confirmed = ensureNoStoredSupplierHasTheSameNameIgnoringCase stored candidate
        return! saveSupplier confirmed
    }
