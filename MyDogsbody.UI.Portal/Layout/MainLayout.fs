module MyDogsbody.UI.Portal.Layout.MainLayout

open Fun.Blazor
open MudBlazor

let view (body: NodeRenderFragment) =
    fragment {
        MudAppBar''{
            Color Color.Primary
            Fixed false
            MudButton'' {
                Color Color.Inherit
                EndIcon Icons.Material.Filled.Home
                Href "/home"
                "Home"
            }
            MudButton''{
                Color Color.Inherit
                EndIcon Icons.Material.Filled.Settings
                Href "/settings"
                "Settings"
            }
        }
        MudMainContent''{
            class' "pt-0 pr-0 pb-0 pl-0"
            body
        }
    }