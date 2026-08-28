module MyDogsbody.UI.Portal.Pages.InvoicesPage

open Fun.Blazor
open Fun.Blazor.Router
open MudBlazor
open MyDogsbody.UI.Types
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators

let private startWork (work: unit -> unit) = Async.Start(async { work () })

let private confirmAndDelete
    (dialogService: IDialogService)
    (m: MyDogsbody.UI.Types.Module.InvoicesModule)
    (invoice: InvoiceUiType) =
    task {
        let! confirmed =
            dialogService.ShowMessageBox(
                title = "Delete invoice",
                message = $"Delete invoice '{invoice.Reference}' from {invoice.SupplierName}? A tombstone keeps the next scan from restoring it (you can un-delete later).",
                yesText = "Delete",
                cancelText = "Cancel"
            )

        if confirmed.HasValue && confirmed.Value then
            m.DeleteInvoice invoice.Id
    }
    :> System.Threading.Tasks.Task
    |> ignore

let getView () =
    html.inject (fun (invoiceApi: InvoiceApi, scanWindowApi: ScanWindowApi, dialogService: IDialogService) ->
        let m =
            InvoicesModuleCreators.getInvoicesModule startWork invoiceApi scanWindowApi

        MudTabs'' {
            Elevation 2
            Rounded true

            MudTabPanel'' {
                Text "Invoices"
                InvoicesComponents.invoicesTable m (confirmAndDelete dialogService m)
            }

            MudTabPanel'' {
                Text "Problems"
                OnClick(fun _ -> m.LoadProblems())
                InvoicesComponents.problemsView m
            }

            MudTabPanel'' {
                Text "Deleted"
                OnClick(fun _ -> m.LoadTombstones())
                InvoicesComponents.tombstonesView m
            }
        })

let getRoute () = getView () |> routeCi "/invoices"
