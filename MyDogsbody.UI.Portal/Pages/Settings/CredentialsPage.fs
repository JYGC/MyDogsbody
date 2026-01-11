module MyDogsbody.UI.Portal.Pages.Settings.CredentialsPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.Compositions.Interfaces
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MudBlazor
open MyDogsbody.UI.Types
open MyDogsbody.Spine.UseCases.Types

let getView () =
    html.inject(fun (credentialCompositions: ICredentialCompositions, dialogService: IDialogService) ->
        let credentialsBrowserModule =
            CredentialsBrowserModuleCreators.getCredentialsBrowserModule
                credentialCompositions.GetAllCredentials
        fragment {
            CredentialsComponents.credentialsBrowser
                credentialsBrowserModule
                (fun _ ->
                    CredentialsComponents.showCredentialsEditorDialog
                        dialogService
                        "Add Credentials"
                        (fun (changedCredentials: IntegrationCredentialUiType) ->
                            changedCredentials
                            |> fun uiType ->
                                let dto: AddCredentialUseCaseTypeDto =
                                    {
                                        InfrastructureType = uiType.InfrastructureType
                                        Credentials = uiType.Credentials
                                        Username = uiType.Username
                                    }
                                dto
                            |> credentialCompositions.AddNewCredential
                        )
                        None
                    |> ignore
                )
                (fun credentials ->
                    CredentialsComponents.showCredentialsEditorDialog
                        dialogService
                        "Edit Credentials"
                        (fun (tt: IntegrationCredentialUiType) -> ())
                        (Some credentials)
                    |> ignore
                )
        }
    )
    |> SettingsComponents.settingsNavMenu


let getRoute () =
    getView ()
    |> routeCi "/settings/credentials"