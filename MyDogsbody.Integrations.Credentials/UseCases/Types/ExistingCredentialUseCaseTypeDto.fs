namespace MyDogsbody.Integrations.Credentials.UseCases.Types

open MyDogsbody.Enums

type ExistingCredentialUseCaseTypeDto =
    {
        Id: string
        InfrastructureType: InfrastructureType
        Credentials: string
        ExternalUsername: string
    }