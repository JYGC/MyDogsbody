namespace MyDogsbody.Exceptions.Types

open System

type MyDogsbodyException(
  businessOperation: string,
  message: string,
  innerException: exn
) =
    inherit Exception(message, innerException)

    let createdDate = DateTime.UtcNow

    member _.BusinessOperation with get() = businessOperation

    member _.CreatedDate with get() = createdDate
