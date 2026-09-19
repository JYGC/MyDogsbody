module MyDogsbody.UI.Portal.Pages.InvoicesPage

open FSharp.Data.Adaptive
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

/// Task 8.5: any plan about to run that contains a delete requires confirmation, listing what
/// would be deleted, before ExecuteSync is called - the guard that makes delete permission
/// trustworthy. A plan with no delete in it runs straight away.
let private confirmAndExecuteSync
    (dialogService: IDialogService)
    (invoicesModule: MyDogsbody.UI.Types.Module.InvoicesModule) =
    let syncView = AVal.force invoicesModule.SyncViewAval
    let selectedInvoiceIds = AVal.force invoicesModule.SelectedInvoiceIdsAval
    let rowsAboutToRun = InvoicesComponents.rowsPendingSync selectedInvoiceIds syncView
    let deletesAboutToRun = rowsAboutToRun |> List.filter (fun row -> row.Action = DeleteSyncAction)

    if List.isEmpty deletesAboutToRun then
        invoicesModule.ExecuteSync()
    else
        let deletedDescriptions =
            deletesAboutToRun
            |> List.map (fun row -> $"{row.SupplierName} - {row.Reference}")
            |> String.concat "; "

        task {
            let! confirmed =
                dialogService.ShowMessageBox(
                    title = "Delete calendar events",
                    message = $"This sync will DELETE {List.length deletesAboutToRun} calendar event(s) whose invoice has left the ledger, and this cannot be undone from here: {deletedDescriptions}. Continue?",
                    yesText = "Delete and sync",
                    cancelText = "Cancel"
                )

            if confirmed.HasValue && confirmed.Value then
                invoicesModule.ExecuteSync()
        }
        :> System.Threading.Tasks.Task
        |> ignore

let getView () =
    html.inject (fun (invoiceApi: InvoiceApi, scanWindowApi: ScanWindowApi, invoiceSyncApi: InvoiceSyncApi, dialogService: IDialogService) ->
        let invoicesModule =
            InvoicesModuleCreators.getInvoicesModule startWork invoiceApi scanWindowApi invoiceSyncApi

        MudTabs'' {
            Elevation 2
            Rounded true

            MudTabPanel'' {
                Text "Invoices"

                fragment {
                    InvoicesComponents.invoicesTable invoicesModule (confirmAndDelete dialogService invoicesModule)
                    InvoicesComponents.calendarSyncSection invoicesModule (fun () -> confirmAndExecuteSync dialogService invoicesModule)
                }
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
