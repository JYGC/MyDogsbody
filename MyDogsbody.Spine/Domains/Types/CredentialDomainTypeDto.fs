namespace MyDogsbody.Spine.Domains.Types

open MyDogsbody.Enums

type CredentialDomainTypeDto =
    {
        Id: string
        InfrastructureType: InfrastructureType
        Credentials: string
        ExternalUsername: string
    }