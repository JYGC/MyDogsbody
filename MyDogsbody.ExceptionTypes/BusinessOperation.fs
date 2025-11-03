namespace MyDogsbody.ExceptionTypes

type Documents =
    | GetContentSplitByLines
    | GetPdfObject

type Infrastructure =
    | Documents of Documents

type BusinessOperation =
    | Infrastructure of Infrastructure