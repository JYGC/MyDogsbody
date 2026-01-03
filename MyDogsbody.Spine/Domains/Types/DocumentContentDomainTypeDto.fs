namespace MyDogsbody.Spine.Domains.Types

type DocumentContentDomainTypeDtoWord =
    {
        Text: string
        Bottom: float
        Left: float
    }

type DocumentContentDomianTypeDto =
    {
        Words: DocumentContentDomainTypeDtoWord list
    }