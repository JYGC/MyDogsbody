namespace MyDogsbody.Spine.UseCases.Types

open MyDogsbody.Enums

type AddCredentialUseCaseTypeDto =
    {
        InfrastructureType: InfrastructureType
        Credentials: string
        Username: string
    }