/// Where the abstract meets the real: the adapters that satisfy each Suppliers dependency
/// function type, the workflows partially applied over them, and the translation between the two
/// error types.
///
/// This is the only place that knows both the main SQLite database and the domain, and the only
/// place the two error types meet. Dependencies are leading parameters, so a test supplies a temp
/// database context; no module-level bindings, so nothing opens a file on import.
module MyDogsbody.Startup.SupplierApiFactory

open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Database
open MyDogsbody.UI.Types

let createSupplierApi
    (handleError: HandleErrorBuilder)
    (databaseContext: DatabaseContext)
    : SupplierApi =

    // Inbound: an adapter becomes a dependency. The store speaks MyDogsbodyException; the
    // workflow is handed a function that speaks SupplierError.
    let loadSuppliers: LoadSuppliers =
        fun () ->
            SupplierStore.getAll
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetSuppliers
                databaseContext.GetSupplierMatchers
                ()
            |> Result.mapError SupplierApiMappers.toSupplierError

    let saveSupplier: SaveSupplier =
        fun supplier ->
            SupplierStore.insertOne
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetSuppliers
                databaseContext.GetSupplierMatchers
                supplier
            |> Result.mapError SupplierApiMappers.toSupplierError

    let updateSupplier: UpdateSupplier =
        fun edit ->
            SupplierStore.updateOne
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetSuppliers
                databaseContext.GetSupplierMatchers
                edit
            |> Result.mapError SupplierApiMappers.toSupplierError

    let deleteSupplierDependency: DeleteSupplier =
        fun id ->
            SupplierStore.deleteOne
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetSuppliers
                id
            |> Result.mapError SupplierApiMappers.toSupplierError

    // Outbound: the workflow's domain error becomes the exception the UI renders.
    let toException = SupplierApiMappers.toMyDogsbodyException

    {
        GetAllSuppliers =
            fun () ->
                ListSuppliersWorkflow.listSuppliers loadSuppliers ()
                |> Result.map (List.map SupplierApiMappers.toUiType)
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.SupplierApi.getAllSuppliers)

        AddSupplier =
            fun uiType ->
                uiType
                // Result.bind, not |>: the mapper rejects an unrecognised matcher kind rather
                // than raising on it, so its failure joins the workflow's on the same track.
                |> SupplierApiMappers.toUnvalidatedSupplier
                |> Result.bind (AddSupplierWorkflow.addSupplier loadSuppliers saveSupplier)
                // The UI does not need the stored supplier back - a write reloads.
                |> Result.map ignore
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.SupplierApi.addSupplier)

        EditSupplier =
            fun uiType ->
                uiType
                |> SupplierApiMappers.toUnvalidatedSupplierEdit
                |> Result.bind (EditSupplierWorkflow.editSupplier loadSuppliers updateSupplier)
                |> Result.map ignore
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.SupplierApi.editSupplier)

        DeleteSupplier =
            fun id ->
                DeleteSupplierWorkflow.deleteSupplier deleteSupplierDependency id
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.SupplierApi.deleteSupplier)
    }
