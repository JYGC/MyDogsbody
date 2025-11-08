module MyDogsbody.UI.Portal.Pages.CredentialsPage

open Fun.Blazor
open Fun.Blazor.Router

let getView () =
    fragment {
        h3 { "Credentials Management" }
        p { "This page will allow you to manage your infrastructure credentials." }
    }

let getRoute () =
    getView () |> routeCi "/credentials"