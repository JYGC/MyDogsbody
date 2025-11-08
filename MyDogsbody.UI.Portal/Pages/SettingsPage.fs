module MyDogsbody.UI.Portal.Pages.SettingsPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MudBlazor

let getView () =
    fragment {
        h3 { "Credentials Management" }
        p { "This page will allow you to manage your infrastructure credentials." }
    }
    |> SubLayouts.settingsNavMenu

let getRoute () =
    getView () |> routeCi "/settings"