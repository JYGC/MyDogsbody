module MyDogsbody.Domain.Suppliers.ListSuppliersWorkflow

open MyDogsbody.Domain.Suppliers

/// Lists the stored suppliers, ordered by name.
///
/// The sort is the workflow's, not the store's, so it is unit-tested without a database: the
/// table must show a stable order regardless of what order the dependency happened to return.
let listSuppliers
    (loadSuppliers: LoadSuppliers)
    ()
    : Result<StoredSupplier list, SupplierError> =
    loadSuppliers ()
    |> Result.map (List.sortBy (fun supplier -> SupplierName.value supplier.Name))
