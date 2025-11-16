namespace MyDogsbody.UI.Types

open MyDogsbody.Enums

type InfrustructureCredential =
    {
        InfrastructureType: InfrastructureType
        Credentials: string
        Username: string
    }
