module MyDogsbody.Exceptions.ExceptionHelpers

open System
open MyDogsbody.ExceptionTypes

let isApplicationException (ex: Exception) =
    ex :? MyDogsbodyException && ex.InnerException :? ApplicationException