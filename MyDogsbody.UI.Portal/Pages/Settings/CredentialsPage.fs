module MyDogsbody.UI.Portal.Pages.Settings.CredentialsPage

open FSharp.Data.Adaptive
open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UseCases.Interfaces
open MyDogsbody.UI.Types
open MyDogsbody.Enums

let getView () =
    html.inject(fun (credentialUseCases: ICredentialUseCases) ->
        let isLoadingCval = cval false
        let credentialsListCval = cval<InfrustructureCredential list> []
        let showModelCval = cval false

        let getCredentials() =
            isLoadingCval.Value <- true
            async {
                let credentialsDtos = credentialUseCases.GetAllCredentials()
                transact(fun _ ->
                    credentialsListCval.Value <-
                        credentialsDtos
                        |> List.map(fun dto ->
                            {
                                InfrastructureType = dto.InfrastructureType
                                Credentials = dto.Credentials
                                Username = dto.Username
                            }
                        )
                    isLoadingCval.Value <- false
                )
            }
            |> Async.Start

        let getDefaultSelectedCredentials() =
            {
                InfrastructureType = InfrastructureType.Google;
                Credentials = "";
                Username = "";
            }

        let selectedCredentialsCval = getDefaultSelectedCredentials() |> cval

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
                credentialsListCval
                isLoadingCval
                showAddCredentialsModal
            CredentialsComponents.credentialsEditor
                "New Credentials"
                modelCancelButton
                modelSubmitButton
                showModelCval
                selectedCredentialsCval
        }
    )
    |> SettingsComponents.settingsNavMenu

let getRoute () =
    getView () |> routeCi "/settings/credentials"