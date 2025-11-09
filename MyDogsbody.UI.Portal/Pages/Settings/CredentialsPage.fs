module MyDogsbody.UI.Portal.Pages.Settings.CredentialsPage

open FSharp.Data.Adaptive
open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UseCases.Interfaces
open MyDogsBody.Dtos

let getView () =
    html.inject(fun (credentialUseCases: ICredentialUseCases) ->
        let isLoadingCval = cval false
        let credentialsCval = cval<InfrustructureCredentialDto list> []
        let showModelCval = cval false

        let getCredentials() =
            isLoadingCval.Value <- true
            async {
                let credentialsList = credentialUseCases.GetAllCredentials()
                transact(fun _ ->
                    credentialsCval.Value <- credentialsList
                    isLoadingCval.Value <- false
                )
            }
            |> Async.Start

        let showAddCredentialsModal() =
            transact(fun _ ->
                showModelCval.Value <- true
            )

        let modelCancelButton _ =
            transact(fun _ ->
                showModelCval.Value <- false
            )

        let modelSubmitButton _ =
            transact(fun _ ->
                showModelCval.Value <- false
            )

        getCredentials()
        
        fragment {
            CredentialsComponents.credentialsBrowser
                credentialsCval
                isLoadingCval
                showAddCredentialsModal
            CredentialsComponents.credentialsEditor
                showModelCval
                "New Credential"
                modelCancelButton
                modelSubmitButton
        }
    )
    |> SettingsComponents.settingsNavMenu

let getRoute () =
    getView () |> routeCi "/settings/credentials"