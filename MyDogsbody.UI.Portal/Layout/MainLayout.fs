module MyDogsbody.UI.Portal.Layout.MainLayout

open Fun.Blazor
open MudBlazor

let view (body: NodeRenderFragment) =
    fragment {
        MudAppBar''{
            Color Color.Primary
            Fixed false
            MudMenu''{
                Dense true
                Variant Variant.Text
                Size Size.Medium
                Color Color.Inherit
                Icon Icons.Material.TwoTone.MoreVert

                MudMenuItem''{
                    Href "/credentials"
                    Icon Icons.Material.TwoTone.LockPerson
                    Label "Credentials"
                }

                MudMenuItem''{
                    Href "/home"
                    Icon Icons.Material.TwoTone.Home
                    Label "Hacker News"
                }
            }
        }
        MudMainContent''{
            class' "pt-2 pr-2 pb-2 pl-2"
            body
        }
    }