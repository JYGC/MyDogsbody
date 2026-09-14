module MyDogsbody.Exceptions.ExceptionHelpers

open System
open MyDogsbody.Exceptions.Types

let isApplicationException (exceptionToCheck: Exception) =
    exceptionToCheck :? MyDogsbodyException && exceptionToCheck.InnerException :? ApplicationException