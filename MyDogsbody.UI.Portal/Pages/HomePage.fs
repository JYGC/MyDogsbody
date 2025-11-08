module MyDogsbody.UI.Portal.Pages.HomePage

open Fun.Blazor
open Fun.Blazor.Router

let getView () = fragment {
    h3 { "Welcome to MyDogsBody!" }
    p { "This is the home page of the MyDogsbody application." }
}

let getDefaultRoute () =
    getView () |> routeCi "/"

let getRoute () =
    getView () |> routeCi "/home"