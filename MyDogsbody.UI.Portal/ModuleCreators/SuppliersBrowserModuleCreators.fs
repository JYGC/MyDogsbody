module MyDogsbody.UI.Portal.ModuleCreators.SuppliersBrowserModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// Builds the suppliers browser state.
///
/// `startWork` is how the module gets off the render thread. Production passes an Async.Start
/// equivalent; a test passes `fun work -> work ()` and never has to wait.
let getSuppliersBrowserModule
  (startWork: (unit -> unit) -> unit)
  (supplierApi: SupplierApi)
  : SuppliersBrowserModule =
    let isLoadingCval = cval false
    let errorCval = cval<string option> None
    let suppliersListCval = cval<SupplierUiType list> []

    let loadSuppliers () =
        transact (fun _ -> isLoadingCval.Value <- true)
        startWork (fun () ->
            let result = supplierApi.GetAllSuppliers()
            transact (fun _ ->
                match result with
                | Ok suppliers ->
                    suppliersListCval.Value <- suppliers
                    errorCval.Value <- None
                | Error ex ->
                    errorCval.Value <- Some ex.Message
                isLoadingCval.Value <- false
            )
        )

    /// Runs a write and reloads, so the table shows what was actually stored rather than what
    /// the dialog was holding.
    let write operation =
        transact (fun _ -> isLoadingCval.Value <- true)
        startWork (fun () ->
            match operation () with
            | Ok () ->
                transact (fun _ ->
                    errorCval.Value <- None
                    isLoadingCval.Value <- false
                )
                loadSuppliers ()
            | Error (ex: MyDogsbody.Exceptions.Types.MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false
                )
        )

    loadSuppliers ()

    {
        SuppliersListAval = suppliersListCval
        IsLoadingAval = isLoadingCval
        ErrorAval = errorCval
        LoadSuppliers = loadSuppliers
        AddSupplier = fun supplier -> write (fun () -> supplierApi.AddSupplier supplier)
        EditSupplier = fun supplier -> write (fun () -> supplierApi.EditSupplier supplier)
        DeleteSupplier = fun id -> write (fun () -> supplierApi.DeleteSupplier id)
    }
