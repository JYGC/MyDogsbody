namespace MyDogsbody.Spine.UseCases.Types

open MyDogsbody.Enums

type CredentialUseCaseTypeDto =
    {
        Id: string
        InfrastructureType: InfrastructureType
        Credentials: string
        Username: string
    }