namespace MyDogsbody.Domain

/// The domain's own Result computation expression.
///
/// This exists because MyDogsbody.Builders.HandleErrorBuilder cannot serve the centre: its
/// Bind returns Result&lt;_, MyDogsbodyException&gt; and its TryWith handler returns one, so its
/// error type is pinned rather than generic - it could never bind a Result&lt;_, SupplierError&gt;.
/// It also lives in a project the domain is not allowed to reference.
///
/// So this is that builder with two things taken out: the writeLog constructor parameter, and
/// the annotations that pin the error type. There is deliberately no TryWith - the domain never
/// catches exceptions, because it never performs the I/O that raises them. An expected failure
/// here is a discriminated union case and was never an exception in the first place.
type ResultBuilder() =

    member _.Bind(priorResult: Result<'T, 'TError>, continuation: 'T -> Result<'U, 'TError>) : Result<'U, 'TError> =
        match priorResult with
        | Ok value -> continuation value
        | Error error -> Error error

    member _.Return(value: 'T) : Result<'T, 'TError> = Ok value

    member _.ReturnFrom(existingResult: Result<'T, 'TError>) : Result<'T, 'TError> = existingResult

    member this.Zero() : Result<unit, 'TError> = this.Return()

    member _.Delay(generateResult: unit -> Result<'T, 'TError>) = generateResult

    member _.Run(generateResult: unit -> Result<'T, 'TError>) : Result<'T, 'TError> = generateResult ()

[<AutoOpen>]
module ResultBuilderInstance =

    /// `result { ... }` - the builder every domain workflow is written with.
    let result = ResultBuilder()
