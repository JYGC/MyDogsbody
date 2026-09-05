module MyDogsbody.UI.Portal.Pages.Settings.GoogleAccountsPage

open Fun.Blazor
open Fun.Blazor.Router
open MudBlazor
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types

/// Keeps API calls off the render thread. The module creator takes this as a parameter so tests
/// can run the same code synchronously.
let private startWork (work: unit -> unit) = Async.Start(async { work () })

/// requirements.md: "ask for confirmation, stating that access remains granted at Google" - so
/// the user is not left believing more happened than did (Q3.6).
let private confirmAndRemove
    (dialogService: IDialogService)
    (m: MyDogsbody.UI.Types.Module.GoogleAccountsBrowserModule)
    (account: GoogleAccountUiType)
    =
    task {
        let! confirmed =
            dialogService.ShowMessageBox(
                title = "Remove Google account",
                message = $"Remove '{account.EmailAddress}'? Its local token and record are deleted, but access remains granted at Google - you can revoke it there.",
                yesText = "Remove",
                cancelText = "Cancel"
            )

        if confirmed.HasValue && confirmed.Value then
            m.RemoveAccount account.Id
    }
    :> System.Threading.Tasks.Task
    |> ignore

let getView () =
    html.inject (fun (googleAccountApi: GoogleAccountApi, dialogService: IDialogService) ->
        let googleAccountsBrowserModule =
            GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule startWork googleAccountApi

        GoogleAccountsComponents.googleAccountsBrowser
            googleAccountsBrowserModule
            (confirmAndRemove dialogService googleAccountsBrowserModule))
    |> SettingsComponents.settingsNavMenu

let getRoute () =
    getView () |> routeCi "/settings/google-accounts"
