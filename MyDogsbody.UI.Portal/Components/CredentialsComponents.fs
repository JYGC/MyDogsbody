module MyDogsbody.UI.Portal.Components.CredentialsComponents

open System
open Fun.Blazor
open MudBlazor
open FSharp.Data.Adaptive
open Microsoft.AspNetCore.Components.Web
open MyDogsbody.Enums
open MyDogsbody.UI.Types

let credentialsBrowser
  (credentialsAval: aval<InfrustructureCredential list>)
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
                RowTemplate (fun (credential: InfrustructureCredential) ->
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
  (infrustructureCredentialAval: aval<InfrustructureCredential>)
  (cancel: MouseEventArgs -> unit)
  (submit: MouseEventArgs -> unit) =
    let infrastructureTypes =
        Enum.GetValues(typeof<InfrastructureType>)
        |> Seq.cast<InfrastructureType>

    let options = new DialogOptions(
        CloseOnEscapeKey = false,
        BackdropClick = false,
        FullWidth = true
    )
    adapt {
        let! showModel = showModelAval
        let! infrustructureCredential = infrustructureCredentialAval

        let infrastructureTypeCval = cval infrustructureCredential.InfrastructureType
        let usernameCval = cval infrustructureCredential.Username
        let credentialsCval = cval infrustructureCredential.Credentials

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
                        adapt {
                            let! infrastructureType, setinfrastructureType = infrastructureTypeCval.WithSetter()
                            MudSelect'' {
                                Label "Infrastructure Type"
                                Variant Variant.Text
                                Value infrastructureType
                                ValueChanged setinfrastructureType
                                fragment {
                                    for infrastructureType in infrastructureTypes do
                                        MudSelectItem'' {
                                            Value infrastructureType
                                            $"{(infrastructureType.ToString())}"
                                        }
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
                        adapt {
                            let! username, setUsername = usernameCval.WithSetter()
                            MudTextField'' {
                                Label "Username"
                                Variant Variant.Text
                                Value username
                                ValueChanged setUsername
                            }
                        }
                    }
                }
                MudGrid'' {
                    MudItem'' {
                        xs 12
                        sm 12
                        md 12
                        adapt {
                            let! credentials, setCredentials = credentialsCval.WithSetter()
                            MudTextField'' {
                                Label "Credentials"
                                Variant Variant.Text
                                Lines 5
                                Value credentials
                                ValueChanged setCredentials
                            }
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
                    $"Ok"
                }
            })
        }
    }