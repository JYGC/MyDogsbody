namespace MyDogsbody.UI.Types

open MyDogsbody.Enums

type IntegrationCredentialUiTypeWithoutId =
    {
        InfrastructureType: InfrastructureType
        Credentials: string
        Username: string
    }

type IntegrationCredentialUiType =
    {
        Id: string
        InfrastructureType: InfrastructureType
        Credentials: string
        Username: string
    }