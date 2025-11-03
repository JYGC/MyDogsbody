namespace MyDogsbody.ExceptionTypes

open System

type MyDogsbodyException(
  businessOperation: string,
  message: string,
  innerException: exn
) =
    inherit Exception(message, innerException)

    member _.BusinessOperation with get() = businessOperation
