module MyDogsbody.Tests.Domain.InvoiceTemplates.InvoiceTemplatesTypesTests

open Xunit
open MyDogsbody.Domain.InvoiceTemplates

// Constrained types: holding one is the proof it was validated, so every create gets its own
// test - one accepted value, one rejected value per rule, and the rejection reason asserted.

[<Fact; Trait("Level", "Unit")>]
let ``TemplateId.create accepts a non-empty identifier and preserves it exactly`` () =
    let actual = TemplateId.create "42"

    match actual with
    | Ok id -> Assert.Equal("42", TemplateId.value id)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``TemplateId.create rejects a missing identifier with a reason`` (entered: string) =
    let actual = TemplateId.create entered

    match actual with
    | Error reason -> Assert.Equal("Template id must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``TemplateName.create accepts a non-empty name and trims surrounding whitespace`` () =
    let actual = TemplateName.create "  Monthly statement  "

    match actual with
    | Ok name -> Assert.Equal("Monthly statement", TemplateName.value name)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``TemplateName.create rejects an empty or whitespace-only name`` (entered: string) =
    let actual = TemplateName.create entered

    match actual with
    | Error reason -> Assert.Equal("Template name must not be empty.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``TemplateName.create rejects a name longer than 100 characters`` () =
    let tooLong = System.String('a', 101)

    let actual = TemplateName.create tooLong

    match actual with
    | Error reason -> Assert.Equal("Template name must be 100 characters or fewer.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``TemplateName.create accepts a name exactly at the 100 character limit`` () =
    let atLimit = System.String('a', 100)

    let actual = TemplateName.create atLimit

    match actual with
    | Ok name -> Assert.Equal(100, (TemplateName.value name).Length)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")
