module MyDogsbody.Exceptions.Types.ActionNames

module MyDogsbody =
    let private myDogsbody = "MyDogsbody"

    module Infrastructure =
        let private infrastructure = $"{myDogsbody}.Infrastructure"

        let getContentSplitByLines = $"{infrastructure}.getContentSplitByLines"
        let getPdfObject = $"{infrastructure}.getPdfObject"

    module Domains =
        let private domains = $"{myDogsbody}.Domains"
        module TypeMappers =
            let private typeMappers = $"{domains}.TypeMappers"
            let mapPdfContentUseCaseTypeDtoToDocumentContentDomianTypeDto =
                $"{typeMappers}.mapPdfContentUseCaseTypeDtoToDocumentContentDomianTypeDto"
            let mapAddCredentialDomainTypeDtoToNewCredentialUseCaseTypeDto =
                $"{typeMappers}.mapAddCredentialDomainTypeDtoToNewCredentialUseCaseTypeDto"
            let mapCredentialDomainTypeDtoToExistingCredentialUseCaseTypeDto =
                $"{typeMappers}.mapCredentialDomainTypeDtoToExistingCredentialUseCaseTypeDto"
            let mapExistingCredentialUseCaseTypeDtoToCredentialDomainTypeDtos =
                $"{typeMappers}.mapExistingCredentialUseCaseTypeDtoToCredentialDomainTypeDtos"

    module Repositories =
        let private repositories = $"{myDogsbody}.Repositories"

        module CredentialRepository =
            let private credentialRepository = $"{repositories}.CredentialRepository"
            let insertOne = $"{credentialRepository}.insertOne"
            let getAll = $"{credentialRepository}.getAll"
            let getAllByType = $"{credentialRepository}.getAllByType"

    module UseCases =
        let private useCases = $"{myDogsbody}.UseCases"
        module UseCaseTypeMappers =
            let private useCaseTypeMappers = $"{useCases}.UseCaseTypeMappers"
            let mapPdfContentDomainTypeDtoToPdfContentUseCaseTypeDto =
                $"{useCaseTypeMappers}.mapPdfContentDomainTypeDtoToPdfContentUseCaseTypeDto"
            let mapAddCredentialUseCaseTypeDtoToNewCredentialUseCaseTypeDto =
                $"{useCaseTypeMappers}.mapAddCredentialUseCaseTypeDtoToNewCredentialUseCaseTypeDto"
            let mapAddCredentialUseCaseTypeDtoToAddCredentialDomainTypeDto =
                $"{useCaseTypeMappers}.mapAddCredentialUseCaseTypeDtoToAddCredentialDomain"
            let mapCredentialUseCaseTypeDtoToCredentialDomainTypeDto =
                $"{useCaseTypeMappers}.mapCredentialUseCaseTypeDtoToCredentialDomainTypeDto"
            let mapExistingCredentialUseCaseTypeDtoToCredentialDomainTypeDtos =
                $"{useCaseTypeMappers}.mapExistingCredentialUseCaseTypeDtoToCredentialDomainTypeDtos"
        module CredentialUseCases =
            let private credentialUseCases = $"{useCases}.CredentialUseCases"
            let insertOne = $"{credentialUseCases}.insertOne"
            let getAll = $"{credentialUseCases}.getAll"
    
    module Integrations =
        let private integrations = $"{myDogsbody}.Integrations"
        module Pdf =
            let private pdf = $"{integrations}.Pdf"
            module UseCases =
                let private useCases = $"{pdf}.UseCases"
                module Types =
                    let private types = $"{useCases}.Types"
                    module Mappers =
                        let private mappers = $"{types}.Mappers"
                        let mapPdfContentDomainTypeDtoToPdfContentUseCaseTypeDto =
                            $"{mappers}.mapPdfContentDomainTypeDtoToPdfContentUseCaseTypeDto"
        module Credentials =
            let private credentials = $"{integrations}.Credentials"
            module Repositories =
                let private repositories = $"{credentials}.Repositories"
                let insertOne = $"{repositories}.insertOne"
                let updateOne = $"{repositories}.updateOne"
                let getAll = $"{repositories}.getAll"
            module UseCases =
                let private useCases = $"{credentials}.UseCases"
                let insertOne = $"{useCases}.insertOne"
                let updateOne = $"{useCases}.updateOne"
                let getAll = $"{useCases}.getAll"
                module Types =
                    let private types = $"{useCases}.Types"
                    module Mappers =
                        let private mappers = $"{types}.Mappers"
                        let mapNewCredentialUseCaseTypeDtoToNewCredentialRepositoryTypeDto =
                            $"{mappers}.mapNewCredentialUseCaseTypeDtoToNewCredentialRepositoryTypeDto"
                        let mapExistingCredentialUseCaseTypeDtoToExistingCredentialRepositoryTypeDto =
                            $"{mappers}.mapExistingCredentialUseCaseTypeDtoToExistingCredentialRepositoryTypeDto"
                        let mapExistingCredentialRepositoryTypeDtoToExistingCredentialUseCaseTypeDto =
                            $"{mappers}.mapExistingCredentialRepositoryTypeDtoToExistingCredentialUseCaseTypeDto"