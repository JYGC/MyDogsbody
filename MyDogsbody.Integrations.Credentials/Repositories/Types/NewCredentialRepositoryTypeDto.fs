namespace MyDogsbody.Integrations.Credentials.Repositories.Types

open MyDogsbody.Enums

type NewCredentialRepositoryTypeDto =
    {
        InfrastructureType: InfrastructureType
        Credentials: string
        ExternalUsername: string
    }