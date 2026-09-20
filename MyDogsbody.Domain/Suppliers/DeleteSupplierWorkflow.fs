module MyDogsbody.Domain.Suppliers.DeleteSupplierWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

let deleteSupplier
    (deleteSupplierDependency: DeleteSupplier)
    (input: string)
    : Result<unit, SupplierError> =
    result {
        let! id = SupplierId.create input |> Result.mapError SupplierIdInvalid
        let! aRowCarryingThatIdentifierWasDeleted = deleteSupplierDependency id

        return!
            if aRowCarryingThatIdentifierWasDeleted then
                Ok ()
            else
                Error (SupplierNotFound id)
    }
