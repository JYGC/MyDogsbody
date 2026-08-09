module MyDogsbody.Domain.Suppliers.DeleteSupplierWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers

/// Deletes an existing supplier. Parses the id, deletes, turns a false result (no row carried
/// that identifier) into SupplierNotFound.
let deleteSupplier
    (deleteSupplierDependency: DeleteSupplier)
    (input: string)
    : Result<unit, SupplierError> =
    result {
        let! id = SupplierId.create input |> Result.mapError SupplierIdInvalid
        let! deleted = deleteSupplierDependency id

        return!
            if deleted then
                Ok ()
            else
                Error (SupplierNotFound id)
    }
