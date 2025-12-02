module MyDogsbody.UI.Portal.Pages.Settings.CredentialsPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UseCases.Interfaces
open MyDogsbody.UI.Types.Module
open MyDogsbody.UI.Portal.ModuleCreators

let getView () =
    html.inject(fun (credentialUseCases: ICredentialUseCases) ->
        let newCredentialEditorModule =
            CredentialEditorModuleCreators.getNewCredentialEditorModule ()
        let credentialsBrowserModule =
            CredentialsBrowserModuleCreators.getCredentialsBrowserModule
                newCredentialEditorModule.ShowCredentialsModal
                credentialUseCases.GetAllCredentials
        fragment {
            CredentialsComponents.credentialsBrowser
                credentialsBrowserModule
            CredentialsComponents.credentialsEditor
                newCredentialEditorModule
        }
    )
    |> SettingsComponents.settingsNavMenu

let getRoute () =
    getView () |> routeCi "/settings/credentials"