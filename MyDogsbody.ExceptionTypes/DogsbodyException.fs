namespace MyDogsbody.ExceptionTypes

open System

type MyDogsbodyException(
  businessOperation: BusinessOperation,
  message: string,
  innerException: exn
) =
    inherit Exception(message, innerException)

    member _.BusinessOperation = businessOperation
