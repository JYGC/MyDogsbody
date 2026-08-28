module MyDogsbody.Tests.Domain.Invoices.ValidateInvoiceWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Invoices.ValidateInvoiceWorkflow

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private supplierId = SupplierId.create "sup-1" |> orFail
let private templateId = TemplateId.create "tpl-1" |> orFail
let private messageId = SourceMessageId.create "msg-1" |> orFail

let private extracted =
    { SupplierId = supplierId
      TemplateId = templateId
      SourceMessageId = messageId
      Reference = "INV 100 200"
      Amount = 249.95m
      Currency = "aud"
      IssueDate = Some(DateTime(2026, 2, 1))
      DueDate = Some(DateTime(2026, 3, 3)) }

let private received = DateTime(2026, 1, 20, 8, 30, 0)

[<Fact; Trait("Level", "Unit")>]
let ``validateInvoice returns Ok with every field converted`` () =
    match validateInvoice received extracted with
    | Ok invoice ->
        Assert.Equal(supplierId, invoice.SupplierId)
        Assert.Equal(templateId, invoice.TemplateId)
        Assert.Equal(messageId, invoice.SourceMessageId)
        Assert.Equal("INV100200", InvoiceReference.value invoice.Reference) // whitespace folded
        Assert.Equal(249.95m, Money.amount invoice.Amount)
        Assert.Equal("AUD", Money.currency invoice.Amount) // upper-cased
        Assert.Equal(Some(DateTime(2026, 2, 1)), invoice.IssueDate |> Option.map InvoiceIssueDate.value)
        Assert.Equal(Some(DateTime(2026, 3, 3)), invoice.DueDate |> Option.map InvoiceDueDate.value)
        Assert.Equal(received, invoice.MessageReceivedAt)
    | Error err -> Assert.Fail($"Expected Ok, got Error {err}")

[<Fact; Trait("Level", "Unit")>]
let ``validateInvoice accepts a missing due date - it is not an error`` () =
    match validateInvoice received { extracted with DueDate = None } with
    | Ok invoice -> Assert.Equal(None, invoice.DueDate)
    | Error err -> Assert.Fail($"Expected Ok, got Error {err}")

[<Fact; Trait("Level", "Unit")>]
let ``validateInvoice accepts a missing issue date`` () =
    match validateInvoice received { extracted with IssueDate = None } with
    | Ok invoice -> Assert.Equal(None, invoice.IssueDate)
    | Error err -> Assert.Fail($"Expected Ok, got Error {err}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``validateInvoice returns InvoiceReferenceInvalid carrying the raw value`` (raw: string) =
    match validateInvoice received { extracted with Reference = raw } with
    | Error(InvoiceReferenceInvalid carried) -> Assert.Equal(raw, carried)
    | other -> Assert.Fail($"Expected InvoiceReferenceInvalid, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateInvoice returns AmountInvalid carrying the raw value`` () =
    let tooBig = Money.MaxAbsAmount + 1m

    match validateInvoice received { extracted with Amount = tooBig } with
    | Error(AmountInvalid carried) -> Assert.Equal(string tooBig, carried)
    | other -> Assert.Fail($"Expected AmountInvalid, got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``validateInvoice returns CurrencyInvalid carrying the raw value`` (raw: string) =
    match validateInvoice received { extracted with Currency = raw } with
    | Error(CurrencyInvalid carried) -> Assert.Equal(raw, carried)
    | other -> Assert.Fail($"Expected CurrencyInvalid, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``validateInvoice drops an implausible due date rather than failing the invoice`` () =
    match validateInvoice received { extracted with DueDate = Some(DateTime(9999, 12, 31)) } with
    | Ok invoice -> Assert.Equal(None, invoice.DueDate)
    | Error err -> Assert.Fail($"Expected Ok with no due date, got Error {err}")
