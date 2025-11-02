namespace MyDogsbody.ExceptionTypes

open System

type ApplicationException(message, businessOperation) =
    inherit Exception(message)
