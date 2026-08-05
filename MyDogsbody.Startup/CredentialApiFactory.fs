/// Where the abstract meets the real: the adapters that satisfy each dependency function type,
/// the workflows partially applied over them, and the translation between the two error types.
///
/// This is the only place that knows both LiteDB and the domain, and the only place the two
/// error types meet. Dependencies are leading parameters, so a test supplies a temp-file
/// database context; no module-level bindings, so nothing opens a file on import.
module MyDogsbody.Startup.CredentialApiFactory

open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Credentials
open MyDogsbody.Integrations.Credentials
open MyDogsbody.Integrations.Credentials.Database.Types
open MyDogsbody.UI.Types

let createCredentialApi
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> CredentialsCollection)
    : CredentialApi =

    // Inbound: an adapter becomes a dependency. The store speaks MyDogsbodyException; the
    // workflow is handed a function that speaks CredentialError.
    let loadCredentials: LoadCredentials =
        fun () ->
            CredentialStore.getAll handleError getCredentialCollection ()
            |> Result.mapError CredentialApiMappers.toCredentialError

    let saveCredential: SaveCredential =
        fun credential ->
            CredentialStore.insertOne handleError getCredentialCollection credential
            |> Result.mapError CredentialApiMappers.toCredentialError

    let updateCredential: UpdateCredential =
        fun edit ->
            CredentialStore.updateOne handleError getCredentialCollection edit
            |> Result.mapError CredentialApiMappers.toCredentialError

    // Outbound: the workflow's domain error becomes the exception the UI renders.
    let toException = CredentialApiMappers.toMyDogsbodyException

    {
        GetAllCredentials =
            fun () ->
                ListCredentialsWorkflow.listCredentials loadCredentials ()
                |> Result.map (List.map CredentialApiMappers.toUiType)
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.CredentialApi.getAllCredentials)

        AddCredential =
            fun uiType ->
                uiType
                |> CredentialApiMappers.toUnvalidatedCredential
                |> AddCredentialWorkflow.addCredential saveCredential
                // The UI does not need the stored credential back - a write reloads.
                |> Result.map ignore
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.CredentialApi.addCredential)

        EditCredential =
            fun uiType ->
                uiType
                |> CredentialApiMappers.toUnvalidatedCredentialEdit
                |> EditCredentialWorkflow.editCredential loadCredentials updateCredential
                |> Result.map ignore
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.CredentialApi.editCredential)
    }
