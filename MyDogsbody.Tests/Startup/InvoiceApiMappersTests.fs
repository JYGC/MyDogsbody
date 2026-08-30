module MyDogsbody.Tests.Startup.InvoiceApiMappersTests

open System
open Xunit
open FSharp.Reflection
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Startup

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private names = Map.ofList [ "1", "Acme Pty Ltd"; "2", "Beta Co" ]
let private supplierId n = SupplierId.create n |> orFail
let private templateId = TemplateId.create "9" |> orFail
let private messageId = SourceMessageId.create "m1" |> orFail

// ============================ toInvoiceUiType ============================

let private storedInvoice (dueDate: DateTime option) : StoredInvoice =
    { Id = InvoiceId.create "55" |> orFail
      ScannedAt = DateTime(2026, 6, 1)
      Invoice =
        { SupplierId = supplierId "1"
          TemplateId = templateId
          SourceMessageId = messageId
          Reference = InvoiceReference.create "INV-1042" |> orFail
          Amount = Money.create 249.95m "AUD" |> orFail
          IssueDate = Some(InvoiceIssueDate.create (DateTime(2026, 2, 1)) |> orFail)
          DueDate = dueDate |> Option.map (fun d -> InvoiceDueDate.create d |> orFail)
          MessageReceivedAt = DateTime(2026, 1, 20) } }

[<Fact; Trait("Level", "Contract")>]
let ``toInvoiceUiType maps every field, joining the supplier name`` () =
    let ui = InvoiceApiMappers.toInvoiceUiType names (storedInvoice (Some(DateTime(2026, 3, 3))))
    Assert.Equal("55", ui.Id)
    Assert.Equal("Acme Pty Ltd", ui.SupplierName)
    Assert.Equal("INV-1042", ui.Reference)
    Assert.Equal(249.95m, ui.Amount)
    Assert.Equal("AUD", ui.Currency)
    Assert.Equal(Some(DateTime(2026, 2, 1)), ui.IssueDate)
    Assert.Equal(Some(DateTime(2026, 3, 3)), ui.DueDate)
    Assert.Equal(DateTime(2026, 1, 20), ui.MessageReceivedAt)
    Assert.True(ui.CanBecomeCalendarEvent)
    Assert.Equal(None, ui.CannotUploadReason)

[<Fact; Trait("Level", "Contract")>]
let ``toInvoiceUiType greys out an invoice with no due date and gives the reason`` () =
    let ui = InvoiceApiMappers.toInvoiceUiType names (storedInvoice None)
    Assert.False(ui.CanBecomeCalendarEvent)
    Assert.Equal(Some InvoiceApiMappers.NoDueDateReason, ui.CannotUploadReason)

[<Fact; Trait("Level", "Contract")>]
let ``toInvoiceUiType names an unknown supplier rather than throwing`` () =
    let ui = InvoiceApiMappers.toInvoiceUiType Map.empty (storedInvoice None)
    Assert.Contains("unknown supplier 1", ui.SupplierName)

// ============================ causeSentence (exhaustive) ============================

let private allCauses: ScanProblemCause list =
    [ NoSupplierMatched
      SeveralSuppliersMatched [ supplierId "1"; supplierId "2" ]
      NoTemplateMatched(supplierId "1")
      RuleFoundNothing(supplierId "1", templateId, "Reference")
      AttachmentUnreadable("invoice.pdf", "no text layer")
      FormatUnsupported("statement.xlsx", "xlsx")
      ValueUnparseable("Amount", "two hundred")
      RuleTimedOutCause(supplierId "1", templateId, "Amount") ]

[<Fact; Trait("Level", "Contract")>]
let ``causeSentence produces a non-empty sentence for every ScanProblemCause case`` () =
    let declared = FSharpType.GetUnionCases(typeof<ScanProblemCause>) |> Array.length
    Assert.Equal(declared, List.length allCauses)

    for cause in allCauses do
        let sentence = InvoiceApiMappers.causeSentence names cause
        Assert.False(String.IsNullOrWhiteSpace sentence)
        Assert.EndsWith(".", sentence)

[<Fact; Trait("Level", "Contract")>]
let ``causeSentence uses the supplier name, not the id`` () =
    let sentence = InvoiceApiMappers.causeSentence names (NoTemplateMatched(supplierId "2"))
    Assert.Contains("Beta Co", sentence)
    Assert.DoesNotContain("supplier '2'", sentence)

// ============================ toMyDogsbodyException (exhaustive, expected/unexpected split) ============================

let private allErrors: InvoiceError list =
    [ SupplierNotRecognised "a@b.test"
      MultipleSuppliersMatched("a@b.test", [ supplierId "1" ])
      NoTemplateForSupplier(supplierId "1")
      TemplateMatchedNothing(templateId, Amount)
      AmountUnparseable(Amount, "x")
      DateUnparseable(IssueDate, "x", "d MMM yyyy")
      DueDateOutOfRange(templateId, DateTime(9999, 12, 31), 30)
      RuleTimedOut(templateId, Reference)
      InvoiceReferenceInvalid "  "
      AmountInvalid "9e99"
      CurrencyInvalid ""
      SupplierGone(supplierId "1")
      ScanWindowInvalid "A scan window must be at least one day."
      ScanWindowAlreadyExists 14
      CannotDeleteLastScanWindow
      ScanWindowNotFound 45
      InvoiceNotFound
      NoAccountSelected
      InvoiceStoreFailed "the disk is full" ]

[<Fact; Trait("Level", "Contract")>]
let ``every InvoiceError case maps to a MyDogsbodyException carrying the action and a sentence`` () =
    let declared = FSharpType.GetUnionCases(typeof<InvoiceError>) |> Array.length
    Assert.Equal(declared, List.length allErrors)
    let action = ActionNames.MyDogsbody.Startup.InvoiceApi.scan

    for error in allErrors do
        let ex = InvoiceApiMappers.toMyDogsbodyException action error
        Assert.Equal(action, ex.ActionName)
        Assert.False(String.IsNullOrWhiteSpace ex.Message)

[<Fact; Trait("Level", "Contract")>]
let ``an expected InvoiceError wraps an ApplicationException (unlogged); a store failure does not`` () =
    let action = ActionNames.MyDogsbody.Startup.InvoiceApi.scan

    let expectedEx = InvoiceApiMappers.toMyDogsbodyException action NoAccountSelected
    Assert.IsType<ApplicationException>(expectedEx.InnerException) |> ignore

    let storeEx = InvoiceApiMappers.toMyDogsbodyException action (InvoiceStoreFailed "boom")
    Assert.Equal("boom", storeEx.Message)
    Assert.Null storeEx.InnerException

[<Fact; Trait("Level", "Contract")>]
let ``toInvoiceError wraps an exception's message as InvoiceStoreFailed`` () =
    let ex = MyDogsbodyException(ActionNames.MyDogsbody.Startup.InvoiceApi.scan, "adapter blew up")
    Assert.Equal(InvoiceStoreFailed "adapter blew up", InvoiceApiMappers.toInvoiceError ex)

// ============================ ScanWindowApiMappers ============================

[<Theory; Trait("Level", "Contract")>]
[<InlineData(1, "mail received in the last day")>]
[<InlineData(14, "mail received in the last 14 days")>]
[<InlineData(180, "mail received in the last 180 days")>]
let ``the scan-window label is composed by the mapper and says what it measures`` (days: int) (expected: string) =
    Assert.Equal(expected, ScanWindowApiMappers.windowLabel days)

[<Fact; Trait("Level", "Contract")>]
let ``toUiType maps id, days and the composed label`` () =
    let window: StoredScanWindow =
        { Id = ScanWindowId.create "3" |> orFail
          Days = ScanWindowDays.create 90 |> orFail }

    let ui = ScanWindowApiMappers.toUiType window
    Assert.Equal("3", ui.Id)
    Assert.Equal(90, ui.Days)
    Assert.Equal("mail received in the last 90 days", ui.Label)
