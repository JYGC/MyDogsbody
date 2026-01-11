namespace MyDogsbody.UI.Types

open MyDogsbody.Enums

type IntegrationCredentialUiType =
    {
        InfrastructureType: InfrastructureType
        Credentials: string
        Username: string
    }