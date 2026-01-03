namespace MyDogsbody.Compositions.Interfaces

open MyDogsbody.Spine.UseCases.Types

type ICredentialCompositions =
    abstract member AddNewCredential: AddCredentialUseCaseTypeDto -> unit
    abstract member EditNewCredential: CredentialUseCaseTypeDto -> unit
    abstract member GetAllCredentials: unit -> CredentialUseCaseTypeDto list
