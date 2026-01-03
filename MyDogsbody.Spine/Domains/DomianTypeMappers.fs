module MyDogsbody.Spine.Domains.DomianTypeMappers

open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Pdf.UseCases.Types
open MyDogsbody.Integrations.Credentials.UseCases.Types
open MyDogsbody.Spine.Domains.Types

let mapPdfContentUseCaseTypeDtoToDocumentContentDomianTypeDto
  (handleError: HandleErrorBuilder)
  (useCaseDto: PdfContentUseCaseTypeDto)
  : Result<DocumentContentDomianTypeDto, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.Domains.TypeMappers.mapPdfContentUseCaseTypeDtoToDocumentContentDomianTypeDto
    handleError {
        try
            return {
                Words =
                    useCaseDto.Words
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
                "Failed to map PdfContentUseCaseTypeDto to DocumentContentDomianTypeDto.",
                ex
            )
    }

let mapAddCredentialDomainTypeDtoToNewCredentialUseCaseTypeDto
  (handleError: HandleErrorBuilder)
  (domainDto: AddCredentialDomainTypeDto)
  : Result<NewCredentialUseCaseTypeDto, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.Domains.TypeMappers.mapAddCredentialDomainTypeDtoToNewCredentialUseCaseTypeDto
    handleError {
        try
            return {
                InfrastructureType = domainDto.InfrastructureType
                Credentials = domainDto.Credentials
                ExternalUsername = domainDto.ExternalUsername
            }
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to map AddCredentialDomainTypeDto to NewCredentialUseCaseTypeDto.",
                ex
            )
    }

let mapCredentialDomainTypeDtoToExistingCredentialUseCaseTypeDto
  (handleError: HandleErrorBuilder)
  (domainDto: CredentialDomainTypeDto)
  : Result<ExistingCredentialUseCaseTypeDto, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.Domains.TypeMappers.mapCredentialDomainTypeDtoToExistingCredentialUseCaseTypeDto
    handleError {
        try
            return {
                Id = domainDto.Id
                InfrastructureType = domainDto.InfrastructureType
                Credentials = domainDto.Credentials
                ExternalUsername = domainDto.ExternalUsername
            }
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to map CredentialDomainTypeDto to ExistingCredentialUseCaseTypeDto.",
                ex
            )
    }

let mapExistingCredentialUseCaseTypeDtoToCredentialDomainTypeDtos
  (handleError: HandleErrorBuilder)
  (useCaseDto: ExistingCredentialUseCaseTypeDto list)
  : Result<CredentialDomainTypeDto list, MyDogsbodyException> =
    let action =
        ActionNames.MyDogsbody.Domains.TypeMappers.mapExistingCredentialUseCaseTypeDtoToCredentialDomainTypeDtos
    handleError {
        try
            return
                useCaseDto
                |> List.map (fun dto ->
                    {
                        Id = dto.Id
                        InfrastructureType = dto.InfrastructureType
                        Credentials = dto.Credentials
                        ExternalUsername = dto.ExternalUsername
                    }
                )
        with ex ->
            return! new MyDogsbodyException(
                action,
                "Failed to map ExistingCredentialUseCaseTypeDto to CredentialDomainTypeDto.",
                ex
            )
    }