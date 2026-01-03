module MyDogsbody.Integrations.Pdf.UseCases.PdfDocumentUseCaseTypeMappers

open MyDogsbody.Integrations.Pdf.Domains.Types
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Pdf.UseCases.Types

let mapPdfContentDomainTypeDtoToPdfContentUseCaseTypeDto
  (handleError: HandleErrorBuilder)
  (domainDto: PdfContentDomainTypeDto)
  : Result<PdfContentUseCaseTypeDto, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.Integrations.Pdf.UseCases.Types.Mappers.mapPdfContentDomainTypeDtoToPdfContentUseCaseTypeDto
    handleError {
        try
            return {
                Words =
                    domainDto.Words
                    |> List.map (fun w ->
                        {
                            Text = w.Text
                            Bottom = w.Bottom
                            Left = w.Left
                        }
                    )
            }
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to map PdfContentDomainTypeDto to PdfContentUseCaseTypeDto.",
                ex
            )
    }