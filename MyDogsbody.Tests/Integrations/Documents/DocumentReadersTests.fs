module MyDogsbody.Tests.Integrations.Documents.DocumentReadersTests

open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Documents

/// A reader that records the source it was handed and returns a line naming itself.
let private recorder (name: string) (calls: ResizeArray<string>) : ReadDocumentText =
    fun source ->
        calls.Add(name)
        Ok [ { Text = $"{name}:{source.Name}"; BlockIndex = 0 } ]

let private source format : DocumentSource =
    { Format = format; Name = "x"; Content = [| 1uy |] }

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Pdf", "pdf")>]
[<InlineData("Word", "word")>]
[<InlineData("PlainText", "plain")>]
[<InlineData("EmailBody", "email")>]
let ``dispatch routes a source to the reader for its format and no other`` (format: string) (expected: string) =
    // Arrange
    let calls = ResizeArray<string>()

    let read =
        DocumentReaders.dispatch
            (recorder "pdf" calls)
            (recorder "word" calls)
            (recorder "plain" calls)
            (recorder "email" calls)

    let documentFormat =
        match format with
        | "Pdf" -> Pdf
        | "Word" -> Word
        | "PlainText" -> PlainText
        | _ -> EmailBody

    // Act
    let actual = read (source documentFormat)

    // Assert - exactly one reader called, and it was the right one
    Assert.Equal<string list>([ expected ], List.ofSeq calls)

    match actual with
    | Ok [ line ] -> Assert.Equal($"{expected}:x", line.Text)
    | other -> Assert.Fail($"Expected the {expected} reader's line, got {other}")
