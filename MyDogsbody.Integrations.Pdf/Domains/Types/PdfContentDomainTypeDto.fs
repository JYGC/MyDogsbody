namespace MyDogsbody.Integrations.Pdf.Domains.Types

type PdfContentDomainTypeDtoWord =
    {
        Text: string
        Bottom: float
        Left: float
    }

type PdfContentDomainTypeDto =
    {
        Words: PdfContentDomainTypeDtoWord list
    }