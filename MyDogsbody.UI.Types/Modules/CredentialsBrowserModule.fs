namespace MyDogsbody.UI.Types.Module

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types

type CredentialsBrowserModule =
    {
        CredentialsListAval: aval<IntegrationCredentialUiType list>
        IsLoadingAval: aval<bool>
        /// The message from the last failed operation, cleared by the next successful one.
        ErrorAval: aval<string option>
        LoadCredentials: unit -> unit
        AddCredential: IntegrationCredentialUiTypeWithoutId -> unit
        EditCredential: IntegrationCredentialUiType -> unit
    }
