namespace MyDogsbody.UseCases.Interfaces

open MyDogsBody.Dtos

type ICredentialUseCases =
    abstract member AddNewCredential: InfrustructureCredentialDto -> unit
    abstract member GetAllCredentials: unit -> InfrustructureCredentialDto list
