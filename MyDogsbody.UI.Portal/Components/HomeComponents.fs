module MyDogsbody.UI.Portal.Components.HomeComponents

open Fun.Blazor
open MudBlazor
open Microsoft.AspNetCore.Components

type HomeEditor() =
    inherit FunComponent()
    
    [<CascadingParameter>]
    member val private __MudDialogInstance : IMudDialogInstance = null with get, set

    override this.Render() =
        html.inject(fun () ->
            fragment {
                MudDialog''{
                    TitleContent (fragment {
                        MudText'' {
                            Typo Typo.h3
                            "Welcome to MyDogsbody!"
                        }
                    })
                    DialogContent (fragment {
                        MudGrid'' {
                            MudItem'' {
                                xs 12
                                sm 12
                                md 12
                                MudText'' {
                                    "This is your central hub for managing all your infrastructure credentials and monitoring logs."
                                }
                            }
                        }
                        MudGrid'' {
                            MudItem'' {
                                xs 12
                                sm 12
                                md 12
                                MudText'' {
                                    "Use the navigation menu on the left to access different settings and features."
                                }
                            }
                        }
                    })
                    DialogActions (fragment {
                        MudButton'' {
                            Variant Variant.Filled
                            Color Color.Primary
                            OnClick (fun _ -> this.__MudDialogInstance.Cancel() |> ignore)
                            "Close"
                        }
                    })
                }
            }
        )