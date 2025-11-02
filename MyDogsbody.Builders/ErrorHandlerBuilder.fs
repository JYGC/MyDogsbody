namespace MyDogsbody.Builders

open System

type ErrorHandlerBuilder(writeLog) =
    member _.Bind(m, f) =
        match m with
        | Ok v -> f v
        | Error e -> Error e

    member _.Return(x) = Ok x

    member this.Delay(body) =
        try body()
        with
        | :? ApplicationException as aex ->
            aex :> exn |> Error
        | ex ->
            writeLog ex
            Error ex