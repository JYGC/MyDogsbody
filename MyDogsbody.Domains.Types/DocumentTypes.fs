namespace MyDogsbody.Domains.Types.DocumentTypes

type DocumentWord =
    {
        Text: string
        Bottom: float
        Left: float
    }

type DocumentObject =
    {
        GetWords: unit -> DocumentWord list
    }