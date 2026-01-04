module MyDogsbody.UI.Portal.Pages.Settings.CredentialsPage

open FSharp.Data.Adaptive
open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.Compositions.Interfaces
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Types.Module
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.Enums

let getView () =
    html.inject(fun (credentialCompositions: ICredentialCompositions) ->
        let addCredentialEditorModule =
            CredentialEditorModuleCreators.getCredentialEditorModule "Add Credentials"
        let modifyCredentialEditorModule =
            CredentialEditorModuleCreators.getCredentialEditorModule "Edit Credentials"
        let credentialsBrowserModule =
            CredentialsBrowserModuleCreators.getCredentialsBrowserModule
                credentialCompositions.GetAllCredentials
        fragment {
            CredentialsComponents.credentialsBrowser
                credentialsBrowserModule
                (fun _ ->
                    transact(fun _ ->
                        addCredentialEditorModule.InfrustructureCredentialCval.Value <- 
                            {
                                InfrastructureType = InfrastructureType.Google
                                Credentials = ""
                                Username = ""
                            }
                        addCredentialEditorModule.IsModelVisibleCval.Value <- true
                    )
                )
                (fun credentials ->
                    transact(fun _ ->
                        modifyCredentialEditorModule.InfrustructureCredentialCval.Value <-
                            credentials
                        modifyCredentialEditorModule.IsModelVisibleCval.Value <- true
                    )
                )
            CredentialsComponents.credentialsEditor
                addCredentialEditorModule
                (fun credentials -> ())
            CredentialsComponents.credentialsEditor
                modifyCredentialEditorModule
                (fun credentials -> ())
        }
    )
    |> SettingsComponents.settingsNavMenu


let getRoute () =
    getView ()
    |> routeCi "/settings/credentials"