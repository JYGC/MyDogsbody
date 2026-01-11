module MyDogsbody.UI.Portal.ModuleCreators.CredentialsBrowserModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module
open MyDogsbody.Spine.UseCases.Types

let getCredentialsBrowserModule
  (getAllCredentials: unit -> CredentialUseCaseTypeDto list)
  : CredentialsBrowserModule =
    let isLoadingCval = cval false
    let credentialsListCval = cval<IntegrationCredentialUiType list> []

    let getCredentials() =
        isLoadingCval.Value <- true
        async {
            let credentialsDtos = getAllCredentials ()
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

    getCredentials()

    {
        CredentialsListAval = credentialsListCval
        IsLoadingAval = isLoadingCval
        LoadCredentials = getCredentials
    }