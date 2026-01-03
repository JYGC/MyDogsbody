namespace MyDogsbody.Integrations.Pdf.UseCases.Types

type PdfContentUseCaseTypeDtoWord =
    {
        Text: string
        Bottom: float
        Left: float
    }

type PdfContentUseCaseTypeDto =
    {
        Words: PdfContentUseCaseTypeDtoWord list
    }