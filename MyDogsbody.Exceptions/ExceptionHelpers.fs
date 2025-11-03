module MyDogsbody.Exceptions.ExceptionHelpers

open System
open MyDogsbody.Exceptions.Types

let isApplicationException (ex: Exception) =
    ex :? MyDogsbodyException && ex.InnerException :? ApplicationException