namespace MyDogsbody.Exceptions.Types

open System

type MyDogsbodyException(
  actionName: string,
  message: string,
  innerException: exn
) =
    inherit Exception(message, innerException)

    member _.ActionName with get() = actionName
