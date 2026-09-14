namespace MyDogsbody.Builders

open MyDogsbody.Exceptions
open MyDogsbody.Exceptions.Types

type HandleErrorBuilder(writeLog) =
    member _.Bind(priorResult, continuation): Result<_,MyDogsbodyException> =
        match priorResult with
        | Ok value -> continuation value
        | Error error -> Error error

    member _.Return(value) = Ok value

    member _.ReturnFrom(result) = result

    member _.Yield(value) = Ok value

    member _.YieldFrom(result) = result

    member this.Zero() = this.Return()

    member _.Delay(deferredComputation) = deferredComputation

    member _.Run(deferredComputation) = deferredComputation()

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
        | caughtApplicationException when (ExceptionHelpers.isApplicationException caughtApplicationException) ->
            caughtApplicationException :?> MyDogsbodyException |> Error
        | caughtException ->
            let translatedException = handler caughtException
            writeLog translatedException
            Error translatedException

    member this.TryFinally(body, compensation) =
        try this.ReturnFrom(body())
        finally compensation()

    member this.Using(disposable:#System.IDisposable, body) =
        let bodyWithDisposable = fun () -> body disposable
        this.TryFinally(bodyWithDisposable, fun () ->
            match disposable with
                | null -> ()
                | nonNullDisposable -> nonNullDisposable.Dispose())

    member this.For(sequence:seq<_>, body) =
        this.Using(sequence.GetEnumerator(),fun enumerator ->
            this.While(enumerator.MoveNext,
                this.Delay(fun () -> body enumerator.Current)))
