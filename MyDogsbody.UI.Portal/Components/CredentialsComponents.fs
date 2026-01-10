module MyDogsbody.UI.Portal.Components.CredentialsComponents

open System
open Fun.Blazor
open MudBlazor
open FSharp.Data.Adaptive
open MyDogsbody.Enums
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module
open Microsoft.AspNetCore.Components

let credentialsBrowser
  (credentialsBrowserModule: CredentialsBrowserModule)
  (showAddCredentialsModal: _ -> unit)
  (showEditCredentialsModal: InfrustructureCredentialUiType -> unit) =
    fragment {
        adapt {
            let! credentials = credentialsBrowserModule.CredentialsListAval
            let! isLoading = credentialsBrowserModule.IsLoadingAval
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
                    adapt {

                        MudButton''{
                            Variant Variant.Filled
                            Color Color.Primary
                            EndIcon Icons.Material.Filled.Add
                            OnClick (fun _ ->
                                showAddCredentialsModal ()
                            )
                            "New Credential"
                        }
                    }
                })
                HeaderContent (
                    fragment {
                        MudTh''{ "Infrastructure Type" }
                        MudTh''{ "Credentials" }
                        MudTh''{ "Username" }
                        MudTh''{ }
                    }
                )
                RowTemplate (fun (credential: InfrustructureCredentialUiType) ->
                    fragment {
                        MudTd''{ $"{credential.InfrastructureType}" }
                        MudTd''{ $"{credential.Credentials}" }
                        MudTd''{ $"{credential.Username}" }
                        MudTd''{
                            MudButton''{
                                Variant Variant.Outlined
                                Color Color.Primary
                                OnClick (fun _ ->
                                    showEditCredentialsModal credential
                                )
                                "Edit"
                            }
                        }
                    }
                )
            }
        }
    }

type CredentialsEditorDialog() =
    inherit FunComponent()
    
    [<CascadingParameter>]
    member val private __mudDialogInstance : IMudDialogInstance = null with get, set

    [<Parameter>]
    member val public Title : string = "Add Credential" with get, set

    [<Parameter>]
    member val public CredentialUiType : InfrustructureCredentialUiType = {
        InfrastructureType = InfrastructureType.Google;
        Credentials = "";
        Username = "" } with get, set

    [<Parameter>]
    member val public GetInfrustructureCredentialCallback : (InfrustructureCredentialUiType -> unit) = fun _ -> () with get, set

    override this.Render() =
        let infrastructureTypes =
            Enum.GetValues(typeof<InfrastructureType>)
            |> Seq.cast<InfrastructureType>

        let infrastructureTypeCval = cval this.CredentialUiType.InfrastructureType
        let usernameCval = cval this.CredentialUiType.Username
        let credentialsCval = cval this.CredentialUiType.Credentials

        fragment {
            MudDialog''{
                TitleContent (fragment {
                    MudText'' {
                        Typo Typo.h6
                        this.Title
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
                                    Immediate true
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
                                    Immediate true
                                    ValueChanged setCredentials
                                }
                            }
                        }
                    }
                })
                DialogActions (fragment {
                    MudButton'' {
                        OnClick (fun _ -> this.__mudDialogInstance.Cancel() |> ignore)
                        "Cancel"
                    }
                    adapt {
                        let! infrastructureType = infrastructureTypeCval
                        let! username = usernameCval
                        let! credentials = credentialsCval
                        let disableOkButton =
                            String.IsNullOrWhiteSpace(username) ||
                            String.IsNullOrWhiteSpace(credentials)
                        MudButton'' {
                            Disabled disableOkButton
                            Color Color.Primary
                            OnClick (fun _ ->
                                this.GetInfrustructureCredentialCallback
                                    {
                                        InfrastructureType = infrastructureType
                                        Username = username
                                        Credentials = credentials
                                    }
                                this.__mudDialogInstance.Close() |> ignore
                            )
                            "Ok"
                        }
                    }
                })
            }
        }