namespace MyDogsbody.Spine.Domains.Types

open MyDogsbody.Enums

type AddCredentialDomainTypeDto =
    {
        InfrastructureType: InfrastructureType
        Credentials: string
        ExternalUsername: string
    }