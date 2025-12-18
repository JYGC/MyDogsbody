namespace MyDogsbody.UI.Types.Module

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types

type CredentialsBrowserModule =
    {
        CredentialsListAval: aval<InfrustructureCredentialUiType list>
        IsLoadingAval: aval<bool>
        LoadCredentials: unit -> unit
    }