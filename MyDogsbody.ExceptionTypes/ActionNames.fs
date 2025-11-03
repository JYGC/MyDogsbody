module MyDogsbody.ExceptionTypes.ActionNames

module MyDogsbody =
    let private myDogsbody = "MyDogsbody"

    module Infrastructure =
        let private infrastructure = $"{myDogsbody}.Infrastructure"

        let getContentSplitByLines = $"{infrastructure}.getPdfObject"
        let getPdfObject = $"{infrastructure}.getPdfObject"