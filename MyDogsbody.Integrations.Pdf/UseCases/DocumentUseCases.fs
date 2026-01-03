module MyDogsbody.Integrations.Pdf.UseCases.DocumentUseCases

open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Pdf.UseCases.Types
open MyDogsbody.Integrations.Pdf.Domains

let getPdfContent
  (handleError: HandleErrorBuilder)
  (pdfPath: string)
  : Result<PdfContentUseCaseTypeDto, MyDogsbodyException> =
    ReadPdfDomain.getPdfContent handleError pdfPath
    |> Result.bind (fun domainDto ->
        PdfDocumentUseCaseTypeMappers.mapPdfContentDomainTypeDtoToPdfContentUseCaseTypeDto handleError domainDto
    )
