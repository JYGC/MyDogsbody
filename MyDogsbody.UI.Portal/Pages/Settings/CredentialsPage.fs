module MyDogsbody.UI.Portal.Pages.Settings.CredentialsPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.Compositions.Interfaces
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MudBlazor
open MyDogsbody.UI.Types

let getView () =
    html.inject(fun (credentialCompositions: ICredentialCompositions, dialogService: IDialogService) ->
        let credentialsBrowserModule =
            CredentialsBrowserModuleCreators.getCredentialsBrowserModule
                credentialCompositions.GetAllCredentials
        fragment {
            CredentialsComponents.credentialsBrowser
                credentialsBrowserModule
                (fun _ ->
                    let parameters = new DialogParameters<CredentialsComponents.CredentialsEditorDialog>()
                    parameters.Add("GetInfrustructureCredentialCallback", (fun (tt: InfrustructureCredentialUiType) -> ()))
                    dialogService.ShowAsync<CredentialsComponents.CredentialsEditorDialog>(
                        "Add Credentials",
                        parameters
                    )
                    |> ignore
                )
                (fun credentials ->
                    let parameters = new DialogParameters<CredentialsComponents.CredentialsEditorDialog>()
                    parameters.Add("CredentialUiType", credentials)
                    parameters.Add("GetInfrustructureCredentialCallback", (fun (tt: InfrustructureCredentialUiType) -> ()))
                    dialogService.ShowAsync<CredentialsComponents.CredentialsEditorDialog>(
                        "Add Credentials",
                        parameters
                    )
                    |> ignore
                )
        }
    )
    |> SettingsComponents.settingsNavMenu


let getRoute () =
    getView ()
    |> routeCi "/settings/credentials"