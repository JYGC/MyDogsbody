module MyDogsbody.UI.Portal.Pages.HomePage

open Fun.Blazor
open Fun.Blazor.Router
open MudBlazor
open MyDogsbody.UI.Portal.Components

let getView () = html.inject(fun (dialogService: IDialogService) ->
    fragment {
        MudText'' {
            Typo Typo.h3
            "Welcome to MyDogsBody!"
        }
        MudText'' { "This is the home page of the MyDogsbody application." }
        MudButton'' {
            Variant MudBlazor.Variant.Filled
            Color MudBlazor.Color.Primary
            OnClick (fun _ ->
                dialogService.ShowAsync<HomeComponents.HomeEditor>()
                |> ignore
            )
            "Open Popup"
        }
    }
)

let getDefaultRoute () =
    getView () |> routeCi "/"

let getRoute () =
    getView () |> routeCi "/home"