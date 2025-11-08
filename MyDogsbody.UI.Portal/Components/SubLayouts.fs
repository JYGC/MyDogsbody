module MyDogsbody.UI.Portal.Components.SubLayouts

open MudBlazor
open Fun.Blazor

let settingsNavMenu(body: NodeRenderFragment) =
    fragment {
        MudStack'' {
            Row true
            MudPaper'' {
                Elevation 0
                Width "200px"
                Height "90vh"
                class' "py-3"
                MudNavMenu'' {
                    Color Color.Info
                    MudNavLink'' {
                        Href "/settings"
                        Icon Icons.Material.TwoTone.Home
                        "Settings"
                    }
                    MudNavLink'' {
                        Href "/settings/credentials"
                        Icon Icons.Material.TwoTone.LockPerson
                        "Credentials"
                    }
                    MudNavGroup'' {
                        Title "Logs"
                        Icon Icons.Material.TwoTone.MonitorHeart
                        Expanded true
                        MudNavLink'' {
                            Href "/settings/exceptionlogs"
                            Icon Icons.Material.TwoTone.Error
                            "Exceptions"
                        }
                    }
                }
            }
            MudMainContent'' {
                class' "pt-2 pr-2 pb-2 pl-2"
                body
            }
        }
    }
