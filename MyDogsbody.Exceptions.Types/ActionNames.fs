module MyDogsbody.Exceptions.Types.ActionNames

module MyDogsbody =
    let private myDogsbody = "MyDogsbody"

    module Infrastructure =
        let private infrastructure = $"{myDogsbody}.Infrastructure"

        let getContentSplitByLines = $"{infrastructure}.getContentSplitByLines"
        let getPdfObject = $"{infrastructure}.getPdfObject"

    module Repositories =
        let private repositories = $"{myDogsbody}.Repositories"

        module CredentialRepository =
            let private credentialRepository = $"{repositories}.CredentialRepository"
            let insertOne = $"{credentialRepository}.insertOne"
            let getAll = $"{credentialRepository}.getAll"
            let getAllByType = $"{credentialRepository}.getAllByType"