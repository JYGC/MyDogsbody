namespace MyDogsbody.UI.Types.Module

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types

type SuppliersBrowserModule =
    {
        SuppliersListAval: aval<SupplierUiType list>
        IsLoadingAval: aval<bool>
        /// The message from the last failed operation, cleared by the next successful one.
        ErrorAval: aval<string option>
        LoadSuppliers: unit -> unit
        AddSupplier: SupplierUiTypeWithoutId -> unit
        EditSupplier: SupplierUiType -> unit
        DeleteSupplier: string -> unit
    }
