namespace MyDogsbody.Exceptions.Types

open System

type MyDogsbodyException(
  actionName: string,
  message: string,
  innerException: exn
) =
    inherit Exception(message, innerException)

    /// For a failure that never was an exception in the first place - a domain error case
    /// translated at the composition root for the UI to render. There is no inner exception to
    /// preserve and no stack trace worth reading: the user simply typed something the domain
    /// rejected.
    new(actionName: string, message: string) = MyDogsbodyException(actionName, message, null)

    member _.ActionName with get() = actionName
