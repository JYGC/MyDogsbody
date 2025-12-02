namespace MyDogsbody.UI.Types.Module

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types

type CredentialsBrowserModule =
    {
        CredentialsListAval: aval<InfrustructureCredential list>
        IsLoadingAval: aval<bool>
        ShowAddCredentialsModal: unit -> unit
    }