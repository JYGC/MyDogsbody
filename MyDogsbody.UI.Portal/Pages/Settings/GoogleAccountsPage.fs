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

let getView () =
    html.inject (fun (googleAccountApi: GoogleAccountApi, dialogService: IDialogService) ->
        let googleAccountsBrowserModule =
            GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule startWork googleAccountApi

        GoogleAccountsComponents.googleAccountsBrowser
            googleAccountsBrowserModule
            (GoogleAccountsComponents.confirmAndRemove dialogService googleAccountsBrowserModule.RemoveAccount))
    |> SettingsComponents.settingsNavMenu

let getRoute () =
    getView () |> routeCi "/settings/google-accounts"
