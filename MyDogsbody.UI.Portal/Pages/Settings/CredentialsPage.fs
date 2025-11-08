module MyDogsbody.UI.Portal.Pages.Settings.CredentialsPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MudBlazor

let getView () =
    fragment {
        h3 { "Settings Page" }
        p { "This page will allow you to ste stuff" }
    }
    |> SettingsComponents.settingsNavMenu

let getRoute () =
    getView () |> routeCi "/settings/credentials"