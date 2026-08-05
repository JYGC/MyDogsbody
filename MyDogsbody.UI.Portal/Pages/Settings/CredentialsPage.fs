module MyDogsbody.UI.Portal.Pages.Settings.CredentialsPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MudBlazor
open MyDogsbody.UI.Types

/// Keeps the credential load off the render thread. The module creator takes this as a
/// parameter so tests can run the same code synchronously.
let private startWork (work: unit -> unit) =
    Async.Start(async { work () })

let getView () =
    html.inject(fun (credentialApi: CredentialApi, dialogService: IDialogService) ->
        let credentialsBrowserModule =
            CredentialsBrowserModuleCreators.getCredentialsBrowserModule
                startWork
                credentialApi
        fragment {
            CredentialsComponents.credentialsBrowser
                credentialsBrowserModule
                (fun _ ->
                    CredentialsComponents.showCredentialsEditorDialog
                        dialogService
                        "Add Credentials"
                        credentialsBrowserModule.AddCredential
                        None
                    |> ignore
                )
                (fun (credential: IntegrationCredentialUiType) ->
                    CredentialsComponents.showCredentialsEditorDialog
                        dialogService
                        "Edit Credentials"
                        // The dialog edits the fields; the Id comes from the row being edited.
                        (fun (changed: IntegrationCredentialUiTypeWithoutId) ->
                            credentialsBrowserModule.EditCredential
                                {
                                    Id = credential.Id
                                    InfrastructureType = changed.InfrastructureType
                                    Credentials = changed.Credentials
                                    Username = changed.Username
                                }
                        )
                        (Some
                            {
                                InfrastructureType = credential.InfrastructureType
                                Credentials = credential.Credentials
                                Username = credential.Username
                            })
                    |> ignore
                )
        }
    )
    |> SettingsComponents.settingsNavMenu


let getRoute () =
    getView ()
    |> routeCi "/settings/credentials"
