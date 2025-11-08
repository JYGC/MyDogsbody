namespace MyDogsbody.UI.Portal

open Fun.Blazor
open MyDogsbody.UI.Portal.Pages
open MyDogsbody.UI.Portal.Layout
open Microsoft.AspNetCore.Components.Web
open MudBlazor

type Shell() =
    inherit FunComponent()

    override _.Render () = html.inject (fun (hook: IComponentHook) -> ErrorBoundary'() {
        ErrorContent(fun ex -> MudPaper'' {
            style {
                padding 10
                margin 20
            }
            Elevation 10
            MudText'' {
                Color Color.Error
                Typo Typo.subtitle1
                "Some error hanppened, you can try to refresh."
            }
            MudAlert'' {
                Severity Severity.Error
                ex.Message
            }
        })
        html.route [
            HomePage.getDefaultRoute()
            HomePage.getRoute()
            CredentialsPage.getRoute()
        ]
        |> MainLayout.view
    })