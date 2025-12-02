module MyDogsbody.UI.Portal.ModuleCreators.CredentialEditorModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module
open MyDogsbody.Enums

let getNewCredentialEditorModule (): CredentialEditorModule = 
    let getDefaultSelectedCredentials() =
        {
            InfrastructureType = InfrastructureType.Google
            Credentials = ""
            Username = ""
        }
    let showModelCval = cval false
    let selectedCredentialsCval = cval (getDefaultSelectedCredentials())
            
    {
        Title = "New Credentials"
        Cancel = fun _ ->
            transact(fun _ ->
                showModelCval.Value <- false
            )
        Submit = fun _ ->
            transact(fun _ ->
                showModelCval.Value <- false
            )
        ShowCredentialsModal = fun _ ->
            transact(fun _ ->
                showModelCval.Value <- true
            )
        IsModelVisibleCval = showModelCval
        InfrustructureCredentialCval = selectedCredentialsCval
    }