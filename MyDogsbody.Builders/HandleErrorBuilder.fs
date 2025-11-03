namespace MyDogsbody.Builders

open MyDogsbody.Exceptions
open MyDogsbody.Exceptions.Types

type HandleErrorBuilder(writeLog) =
    member _.Bind(m, f): Result<_,MyDogsbodyException> =
        match m with
        | Ok v -> f v
        | Error e -> Error e

    member _.Return(x) = Ok x

    member _.ReturnFrom(x) = x

    member _.Yield(x) = Ok x

    member _.YieldFrom(x) = x

    member this.Zero() = this.Return()

    member _.Delay(f) = f

    member _.Run(f) = f()

    member this.While(guard, body) =
        if not (guard())
        then this.Zero()
        else this.Bind(body(), fun () ->
            this.While(guard, body))

    member this.TryWith(
      body,
      handler: exn -> MyDogsbodyException
    ): Result<_,MyDogsbodyException> =
        try this.ReturnFrom(body())
        with
        | aex when (ExceptionHelpers.isApplicationException aex) ->
            aex :?> MyDogsbodyException |> Error
        | ex ->
            let dex = handler ex
            writeLog dex
            Error dex

    member this.TryFinally(body, compensation) =
        try this.ReturnFrom(body())
        finally compensation()

    member this.Using(disposable:#System.IDisposable, body) =
        let body' = fun () -> body disposable
        this.TryFinally(body', fun () ->
            match disposable with
                | null -> ()
                | disp -> disp.Dispose())

    member this.For(sequence:seq<_>, body) =
        this.Using(sequence.GetEnumerator(),fun enum ->
            this.While(enum.MoveNext,
                this.Delay(fun () -> body enum.Current)))