namespace MyDogsbody.UI.Types.Module

open MyDogsbody.UI.Types
open FSharp.Data.Adaptive

type CredentialEditorModule =
    {
        Title: string
        IsModelVisibleCval: cval<bool>
        InfrustructureCredentialCval: cval<InfrustructureCredentialUiType>
    }