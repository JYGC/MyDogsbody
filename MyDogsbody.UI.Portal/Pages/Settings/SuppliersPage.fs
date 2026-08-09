module MyDogsbody.UI.Portal.Pages.Settings.SuppliersPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MudBlazor
open MyDogsbody.UI.Types

/// Keeps the supplier load off the render thread. The module creator takes this as a parameter
/// so tests can run the same code synchronously.
let private startWork (work: unit -> unit) =
    Async.Start(async { work () })

let private confirmAndDelete
  (dialogService: IDialogService)
  (suppliersBrowserModule: MyDogsbody.UI.Types.Module.SuppliersBrowserModule)
  (supplier: SupplierUiType) =
    task {
        let! confirmed =
            dialogService.ShowMessageBox(
                title = "Delete supplier",
                message = $"Delete '{supplier.Name}'? This cannot be undone.",
                yesText = "Delete",
                cancelText = "Cancel"
            )

        if confirmed.HasValue && confirmed.Value then
            suppliersBrowserModule.DeleteSupplier supplier.Id
    }
    :> System.Threading.Tasks.Task
    |> ignore

let getView () =
    html.inject(fun (supplierApi: SupplierApi, dialogService: IDialogService) ->
        let suppliersBrowserModule =
            SuppliersBrowserModuleCreators.getSuppliersBrowserModule
                startWork
                supplierApi
        fragment {
            SuppliersComponents.suppliersBrowser
                suppliersBrowserModule
                (fun _ ->
                    SuppliersComponents.showSuppliersEditorDialog
                        dialogService
                        "Add Supplier"
                        suppliersBrowserModule.AddSupplier
                        None
                    |> ignore
                )
                (fun (supplier: SupplierUiType) ->
                    SuppliersComponents.showSuppliersEditorDialog
                        dialogService
                        "Edit Supplier"
                        // The dialog edits the fields; the Id comes from the row being edited.
                        (fun (changed: SupplierUiTypeWithoutId) ->
                            suppliersBrowserModule.EditSupplier
                                {
                                    Id = supplier.Id
                                    Name = changed.Name
                                    PaymentTermDays = changed.PaymentTermDays
                                    Matchers = changed.Matchers
                                }
                        )
                        (Some
                            {
                                Name = supplier.Name
                                PaymentTermDays = supplier.PaymentTermDays
                                Matchers = supplier.Matchers
                            })
                    |> ignore
                )
                (confirmAndDelete dialogService suppliersBrowserModule)
        }
    )
    |> SettingsComponents.settingsNavMenu


let getRoute () =
    getView ()
    |> routeCi "/settings/suppliers"
