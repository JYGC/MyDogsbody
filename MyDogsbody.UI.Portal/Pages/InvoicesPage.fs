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
    (invoicesModule: MyDogsbody.UI.Types.Module.InvoicesModule)
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
            invoicesModule.DeleteInvoice invoice.Id
    }
    :> System.Threading.Tasks.Task
    |> ignore

let getView () =
    html.inject (fun (invoiceApi: InvoiceApi, scanWindowApi: ScanWindowApi, dialogService: IDialogService) ->
        let invoicesModule =
            InvoicesModuleCreators.getInvoicesModule startWork invoiceApi scanWindowApi

        MudTabs'' {
            Elevation 2
            Rounded true

            MudTabPanel'' {
                Text "Invoices"
                InvoicesComponents.invoicesTable invoicesModule (confirmAndDelete dialogService invoicesModule)
            }

            MudTabPanel'' {
                Text "Problems"
                OnClick(fun _ -> invoicesModule.LoadProblems())
                InvoicesComponents.problemsView invoicesModule
            }

            MudTabPanel'' {
                Text "Deleted"
                OnClick(fun _ -> invoicesModule.LoadTombstones())
                InvoicesComponents.tombstonesView invoicesModule
            }
        })

let getRoute () = getView () |> routeCi "/invoices"
