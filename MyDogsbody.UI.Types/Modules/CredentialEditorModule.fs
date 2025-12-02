namespace MyDogsbody.UI.Types.Module

open MyDogsbody.UI.Types
open FSharp.Data.Adaptive

type CredentialEditorModule =
    {
        Title: string
        Cancel: unit -> unit
        Submit: InfrustructureCredential -> unit
        ShowCredentialsModal: unit -> unit
        IsModelVisibleCval: cval<bool>
        InfrustructureCredentialCval: cval<InfrustructureCredential>
    }