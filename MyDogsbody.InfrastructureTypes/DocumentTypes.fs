module MyDogsbody.InfrastructureTypes.DocumentTypes

type DocumentWord =
    {
        Text: string
        Bottom: float
        Left: float
    }

type DocumentObject =
    {
        getWords: unit -> DocumentWord list
    }