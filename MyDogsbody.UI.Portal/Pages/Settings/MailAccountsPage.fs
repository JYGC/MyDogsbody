module MyDogsbody.UI.Portal.Pages.Settings.MailAccountsPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types

/// Keeps API calls off the render thread. The module creator takes this as a parameter so tests
/// can run the same code synchronously.
let private startWork (work: unit -> unit) = Async.Start(async { work () })

let getView () =
    html.inject (fun (mailAccountApi: MailAccountApi, folderPicker: FolderPicker) ->
        let mailAccountsBrowserModule =
            MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule startWork mailAccountApi

        MailAccountsComponents.mailAccountsBrowser mailAccountsBrowserModule folderPicker)
    |> SettingsComponents.settingsNavMenu

let getRoute () =
    getView () |> routeCi "/settings/mail-accounts"
