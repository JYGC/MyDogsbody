module MyDogsbody.UI.Portal.Components.CredentialsComponents

open System
open Fun.Blazor
open MudBlazor
open FSharp.Data.Adaptive
open MyDogsBody.Dtos
open Microsoft.AspNetCore.Components.Web
open MyDogsbody.Enums

let credentialsBrowser
  (credentialsAval: aval<InfrustructureCredentialDto list>)
  (isLoadingAval: aval<bool>)
  (showAddCredentialsModal: unit -> unit) =
    fragment {
        adapt {
            let! credentials = credentialsAval
            let! isLoading = isLoadingAval
            MudTable''{
                Items credentials
                Breakpoint Breakpoint.Sm
                Loading isLoading
                FixedHeader true
                LoadingProgressColor Color.Info
                Striped true
                Height "80vh"
                ToolBarContent (fragment {
                    MudText''{
                        Typo Typo.h3
                        "Credentials"
                    }
                    MudSpacer''{}
                    MudButton''{
                        Variant Variant.Filled
                        Color Color.Primary
                        EndIcon Icons.Material.Filled.Add
                        OnClick (fun _ -> showAddCredentialsModal())
                        "New Credential"
                    }
                })
                HeaderContent (
                    fragment {
                        MudTh''{ "Infrastructure Type" }
                        MudTh''{ "Credentials" }
                        MudTh''{ "Username" }
                    }
                )
                RowTemplate (fun (credential: InfrustructureCredentialDto) ->
                    fragment {
                        MudTd''{ $"{credential.InfrastructureType}" }
                        MudTd''{ $"{credential.Credentials}" }
                        MudTd''{ $"{credential.Username}" }
                    }
                )
            }
        }
    }

let credentialsEditor
  (showModelAval: aval<bool>)
  (title: string)
  (cancel: MouseEventArgs -> unit)
  (submit: MouseEventArgs -> unit) =
    let options = new DialogOptions(
        CloseOnEscapeKey = false,
        BackdropClick = false,
        FullWidth = true
    )

    let infrastructureTypes =
        Enum.GetValues(typeof<InfrastructureType>)
        |> Seq.cast<InfrastructureType>

    let infrastructureTypeCval = cval<InfrastructureType>
    let credentialsCval = cval ""
    let usernameCval = cval ""
    adapt {
        let! showModel = showModelAval
        MudDialog''{
            Visible showModel
            Options options
            TitleContent (fragment {
                MudText'' {
                    Typo Typo.h6
                    title
                }
            })
            DialogContent (fragment {
                MudGrid'' {
                    MudItem'' {
                        xs 12
                        sm 12
                        md 12
                        MudSelect'' {
                            Label "Infrastructure Type"
                            Variant Variant.Text
                            fragment {
                                for infrastructureType in infrastructureTypes do
                                    MudSelectItem'' {
                                        $"{(infrastructureType.ToString())}"
                                    }
                            }
                        }
                    }
                }
                MudGrid'' {
                    MudItem'' {
                        xs 12
                        sm 12
                        md 12
                        MudTextField'' {
                            Label "Username"
                            Variant Variant.Text
                        }
                    }
                }
                MudGrid'' {
                    MudItem'' {
                        xs 12
                        sm 12
                        md 12
                        MudTextField'' {
                            Label "Credentials"
                            Variant Variant.Text
                            Lines 5
                        }
                    }
                }
            })
            DialogActions (fragment {
                MudButton'' {
                    OnClick cancel
                    "Cancel"
                }
                MudButton'' {
                    Color Color.Primary
                    OnClick submit
                    "Ok"
                }
            })
        }
    }