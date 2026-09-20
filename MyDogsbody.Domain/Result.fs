namespace MyDogsbody.Domain

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - Result.fs: ResultBuilder
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
