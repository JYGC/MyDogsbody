namespace MyDogsbody.Domains.Types.DocumentTypes

type DocumentWord =
    {
        Text: string
        Bottom: float
        Left: float
    }

type DocumentHandler =
    {
        GetWords: unit -> DocumentWord list
    }