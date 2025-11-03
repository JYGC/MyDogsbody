module MyDogsbody.Exceptions.Types.ActionNames

module MyDogsbody =
    let private myDogsbody = "MyDogsbody"

    module Infrastructure =
        let private infrastructure = $"{myDogsbody}.Infrastructure"

        let getContentSplitByLines = $"{infrastructure}.getContentSplitByLines"
        let getPdfObject = $"{infrastructure}.getPdfObject"