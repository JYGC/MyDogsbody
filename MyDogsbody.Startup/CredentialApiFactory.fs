/// Builds the UI-facing CredentialApi from its dependencies.
///
/// Dependencies are leading parameters, so a test can supply a temp-file database context.
/// No module-level bindings — see Startup.fs for the ones that own real resources.
module MyDogsbody.Startup.CredentialApiFactory

open MyDogsbody.Builders
open MyDogsbody.Integrations.Credentials.Database.Types
open MyDogsbody.Spine.UseCases
open MyDogsbody.UI.Types

let createCredentialApi
  (handleError: HandleErrorBuilder)
  (getCredentialCollection: unit -> CredentialsCollection)
  : CredentialApi =
    {
        GetAllCredentials =
            fun () ->
                CredentialUseCases.getAllCredentials
                    handleError
                    getCredentialCollection
                |> Result.map (List.map CredentialApiMappers.toUiType)

        AddCredential =
            fun uiType ->
                uiType
                |> CredentialApiMappers.toAddCredentialUseCaseTypeDto
                |> CredentialUseCases.addNewCredential
                    handleError
                    getCredentialCollection

        EditCredential =
            fun uiType ->
                uiType
                |> CredentialApiMappers.toCredentialUseCaseTypeDto
                |> CredentialUseCases.editCredential
                    handleError
                    getCredentialCollection
    }
