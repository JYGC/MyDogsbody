namespace MyDogsbody.Integrations.Credentials.UseCases.Types

open MyDogsbody.Enums

type NewCredentialUseCaseTypeDto =
    {
        InfrastructureType: InfrastructureType
        Credentials: string
        ExternalUsername: string
    }