namespace MyDogsbody.Integrations.Credentials.Repositories.Types

open MyDogsbody.Enums

type ExistingCredentialRepositoryTypeDto =
    {
        Id: string
        InfrastructureType: InfrastructureType
        Credentials: string
        ExternalUsername: string
    }