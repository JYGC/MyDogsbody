namespace MyDogsbody.Compositions

open MyDogsbody.Compositions.Interfaces
open MyDogsbody.Spine.UseCases.Types
open MyDogsbody.Spine.UseCases

type CredentialCompositions() =
    interface ICredentialCompositions with
        member _.AddNewCredential(credential: AddCredentialUseCaseTypeDto) =
            CredentialUseCases.addNewCredential
                SetupDatabases.handleError
                SetupDatabases.credentialDatabaseContext.GetCredentialCollection
                credential
            |> ignore
        member _.EditNewCredential(credential: CredentialUseCaseTypeDto) =
            CredentialUseCases.editCredential
                SetupDatabases.handleError
                SetupDatabases.credentialDatabaseContext.GetCredentialCollection
                credential
            |> ignore
        member _.GetAllCredentials() =
            let credentialsResult =
                CredentialUseCases.getAllCredentials
                    SetupDatabases.handleError
                    SetupDatabases.credentialDatabaseContext.GetCredentialCollection
            match credentialsResult with
            | Ok credentials -> credentials
            | Error ex -> failwith ex.Message