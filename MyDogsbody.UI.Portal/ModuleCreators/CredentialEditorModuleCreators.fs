module MyDogsbody.UI.Portal.ModuleCreators.CredentialEditorModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module
open MyDogsbody.Enums

let getCredentialEditorModule (title: string): CredentialEditorModule = 
    let showModelCval = cval false
    let credentialsCval: cval<InfrustructureCredentialUiType> =
        {
            InfrastructureType = InfrastructureType.Google
            Credentials = ""
            Username = ""
        }
        |> cval
    
    {
        Title = title
        IsModelVisibleCval = showModelCval
        InfrustructureCredentialCval = credentialsCval
    }