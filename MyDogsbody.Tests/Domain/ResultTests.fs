module MyDogsbody.Tests.Domain.ResultTests

open Xunit
open MyDogsbody.Domain

/// Two unrelated error types, so the builder's genericity is proved by compilation rather
/// than asserted. HandleErrorBuilder cannot bind either of these - that constraint is the
/// whole reason MyDogsbody.Domain/Result.fs exists.
type private FirstError =
    | FirstFailed of string

type private SecondError =
    | SecondFailed of int

[<Fact; Trait("Level", "Unit")>]
let ``result returns a value as Ok`` () =
    // Act
    let actual: Result<int, FirstError> = result { return 42 }

    // Assert
    Assert.Equal(Ok 42, actual)

[<Fact; Trait("Level", "Unit")>]
let ``result binds an Ok value and passes the unwrapped value on`` () =
    // Arrange
    let step (x: int) : Result<int, FirstError> = Ok (x * 2)

    // Act
    let actual =
        result {
            let! first = step 3
            let! second = step first
            return second + 1
        }

    // Assert
    Assert.Equal(Ok 13, actual)

[<Fact; Trait("Level", "Unit")>]
let ``result short-circuits on Error without evaluating the continuation`` () =
    // Arrange
    let mutable continuationRan = false

    // Act
    let actual =
        result {
            let! _ = Error (FirstFailed "stop here")
            continuationRan <- true
            return 1
        }

    // Assert
    Assert.Equal(Error (FirstFailed "stop here"), actual)
    Assert.False(continuationRan, "the continuation must not run after an Error")

[<Fact; Trait("Level", "Unit")>]
let ``result returns the first Error unchanged when several steps could fail`` () =
    // Arrange
    let failing: Result<int, FirstError> = Error (FirstFailed "first")
    let alsoFailing: Result<int, FirstError> = Error (FirstFailed "second")

    // Act
    let actual =
        result {
            let! a = failing
            let! b = alsoFailing
            return a + b
        }

    // Assert - the first Error wins and is carried out unchanged
    Assert.Equal(Error (FirstFailed "first"), actual)

[<Fact; Trait("Level", "Unit")>]
let ``result forwards an existing Result with return!`` () =
    // Act
    let ok: Result<string, FirstError> = result { return! Ok "forwarded" }
    let error: Result<string, FirstError> = result { return! Error (FirstFailed "forwarded") }

    // Assert
    Assert.Equal(Ok "forwarded", ok)
    Assert.Equal(Error (FirstFailed "forwarded"), error)

[<Fact; Trait("Level", "Unit")>]
let ``result is generic in its error type`` () =
    // Arrange / Act - the same builder over an unrelated error type. This test exists to
    // fail at compile time if Result.fs ever pins its error type the way HandleErrorBuilder does.
    let first: Result<int, FirstError> = result { return! Error (FirstFailed "a") }
    let second: Result<int, SecondError> = result { return! Error (SecondFailed 7) }

    // Assert
    Assert.Equal(Error (FirstFailed "a"), first)
    Assert.Equal(Error (SecondFailed 7), second)

[<Fact; Trait("Level", "Unit")>]
let ``result supports a unit-returning body via Zero`` () =
    // Arrange
    let mutable sideEffect = 0

    // Act
    let actual: Result<unit, FirstError> =
        result {
            sideEffect <- 1
        }

    // Assert
    Assert.Equal(Ok (), actual)
    Assert.Equal(1, sideEffect)
