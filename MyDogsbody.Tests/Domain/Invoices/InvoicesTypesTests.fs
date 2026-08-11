module MyDogsbody.Tests.Domain.Invoices.InvoicesTypesTests

open Xunit
open MyDogsbody.Domain.Invoices

[<Fact; Trait("Level", "Unit")>]
let ``SourceMessageId.create accepts a non-empty identifier and preserves it exactly`` () =
    let actual = SourceMessageId.create "message-42"

    match actual with
    | Ok id -> Assert.Equal("message-42", SourceMessageId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``SourceMessageId.create rejects a missing identifier with a reason`` (entered: string) =
    let actual = SourceMessageId.create entered

    match actual with
    | Error reason -> Assert.Equal("Source message id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
