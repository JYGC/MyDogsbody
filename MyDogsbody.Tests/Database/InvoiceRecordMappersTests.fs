module MyDogsbody.Tests.Database.InvoiceRecordMappersTests

open System
open Xunit
open FSharp.Reflection
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database.Models
open MyDogsbody.Database.InvoiceRecordMappers

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private supplierId = SupplierId.create "7" |> orFail
let private templateId = TemplateId.create "3" |> orFail
let private messageId = SourceMessageId.create "msg-42" |> orFail

let private validInvoice: ValidInvoice =
    { SupplierId = supplierId
      TemplateId = templateId
      SourceMessageId = messageId
      Reference = InvoiceReference.create "INV-1042" |> orFail
      Amount = Money.create 249.95m "AUD" |> orFail
      IssueDate = Some(InvoiceIssueDate.create (DateTime(2026, 2, 1)) |> orFail)
      DueDate = Some(InvoiceDueDate.create (DateTime(2026, 3, 3)) |> orFail)
      MessageReceivedAt = DateTime(2026, 1, 20, 8, 30, 15) }

// ============================ invoice round trip (both directions) ============================

[<Fact; Trait("Level", "Contract")>]
let ``toNewInvoiceRecord maps every field domain -> persistence`` () =
    let record = toNewInvoiceRecord validInvoice
    Assert.Equal(0, record.Id)
    Assert.Equal(7, record.SupplierId)
    Assert.Equal(3, record.TemplateId)
    Assert.Equal("INV-1042", record.Reference)
    Assert.Equal("249.95", record.Amount)
    Assert.Equal("AUD", record.Currency)
    Assert.Equal(Some "2026-02-01", record.IssueDate)
    Assert.Equal(Some "2026-03-03", record.DueDate)
    Assert.Equal("msg-42", record.SourceMessageId)
    Assert.Equal("2026-01-20T08:30:15.0000000", record.MessageReceivedAt)

[<Fact; Trait("Level", "Contract")>]
let ``toStoredInvoice maps every field persistence -> domain and a constrained type survives the string column`` () =
    let record =
        { toNewInvoiceRecord validInvoice with Id = 55; ScannedAt = "2026-06-01T12:00:00.0000000" }

    match toStoredInvoice record with
    | Ok stored ->
        Assert.Equal("55", InvoiceId.value stored.Id)
        Assert.Equal(DateTime(2026, 6, 1, 12, 0, 0), stored.ScannedAt)
        Assert.Equal("7", SupplierId.value stored.Invoice.SupplierId)
        Assert.Equal("3", TemplateId.value stored.Invoice.TemplateId)
        Assert.Equal("msg-42", SourceMessageId.value stored.Invoice.SourceMessageId)
        Assert.Equal("INV-1042", InvoiceReference.value stored.Invoice.Reference) // survived the TEXT column
        Assert.Equal(249.95m, Money.amount stored.Invoice.Amount)
        Assert.Equal("AUD", Money.currency stored.Invoice.Amount)
        Assert.Equal(Some(DateTime(2026, 2, 1)), stored.Invoice.IssueDate |> Option.map InvoiceIssueDate.value)
        Assert.Equal(Some(DateTime(2026, 3, 3)), stored.Invoice.DueDate |> Option.map InvoiceDueDate.value)
        Assert.Equal(DateTime(2026, 1, 20, 8, 30, 15), stored.Invoice.MessageReceivedAt)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Contract")>]
let ``toStoredInvoice keeps a null due date as None`` () =
    let record =
        { toNewInvoiceRecord { validInvoice with DueDate = None; IssueDate = None } with
            Id = 1
            ScannedAt = "2026-06-01T00:00:00.0000000" }

    match toStoredInvoice record with
    | Ok stored ->
        Assert.Equal(None, stored.Invoice.DueDate)
        Assert.Equal(None, stored.Invoice.IssueDate)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Contract")>]
let ``toStoredInvoice reports an unparseable amount rather than raising`` () =
    let record =
        { toNewInvoiceRecord validInvoice with Id = 1; Amount = "not a number"; ScannedAt = "2026-06-01T00:00:00.0000000" }

    match toStoredInvoice record with
    | Error reason -> Assert.Contains("not a number", reason)
    | Ok _ -> Assert.Fail("Expected Error")

// ============================ ScanProblemCause round trip (exhaustive) ============================

let private allCauses: ScanProblemCause list =
    [ NoSupplierMatched
      SeveralSuppliersMatched [ SupplierId.create "1" |> orFail; SupplierId.create "2" |> orFail ]
      NoTemplateMatched supplierId
      RuleFoundNothing(supplierId, templateId, "Reference")
      AttachmentUnreadable("invoice.pdf", "no text layer")
      FormatUnsupported("statement.xlsx", "xlsx")
      ValueUnparseable("Amount", "two hundred")
      RuleTimedOutCause(supplierId, templateId, "Amount") ]

[<Fact; Trait("Level", "Contract")>]
let ``every ScanProblemCause case round-trips through its persisted encoding`` () =
    for cause in allCauses do
        let name, detail, supplierRowId = encodeCause cause

        match decodeCause name detail supplierRowId with
        | Ok decoded -> Assert.Equal(cause, decoded)
        | Error reason -> Assert.Fail($"{cause} did not round-trip: {reason}")

[<Fact; Trait("Level", "Contract")>]
let ``the exhaustive cause list covers every ScanProblemCause union case`` () =
    // Fails if a ninth case is added without a sample here (and, because encodeCause is a match,
    // the build already fails if it has no encoding).
    let declared = FSharpType.GetUnionCases(typeof<ScanProblemCause>) |> Array.length

    let covered =
        allCauses
        |> List.map (fun c -> (fst (FSharpValue.GetUnionFields(c, typeof<ScanProblemCause>))).Tag)
        |> List.distinct
        |> List.length

    Assert.Equal(declared, covered)

[<Fact; Trait("Level", "Contract")>]
let ``decodeCause reports an unknown cause string rather than raising`` () =
    match decodeCause "SomethingNewInV2" None None with
    | Error reason -> Assert.Contains("SomethingNewInV2", reason)
    | Ok _ -> Assert.Fail("Expected Error")

[<Fact; Trait("Level", "Contract")>]
let ``a scan problem round-trips through toNewScanProblemRecord / toScanProblem`` () =
    let problem: ScanProblem =
        { SourceMessageId = messageId
          Sender = "billing@acme.test"
          Subject = "Invoice INV-1042"
          ReceivedAt = DateTime(2026, 1, 20, 8, 30, 0)
          Cause = RuleFoundNothing(supplierId, templateId, "DueDate")
          RecordedAt = DateTime(2026, 6, 1, 12, 0, 0) }

    let record = { toNewScanProblemRecord problem with Id = 9 }

    match toScanProblem record with
    | Ok decoded -> Assert.Equal(problem, decoded)
    | Error reason -> Assert.Fail($"did not round-trip: {reason}")

// ============================ tombstone + scan window ============================

[<Fact; Trait("Level", "Contract")>]
let ``a tombstone round-trips both directions`` () =
    let tombstone: InvoiceTombstone =
        { SupplierId = supplierId
          Reference = InvoiceReference.create "INV-1042" |> orFail
          DeletedAt = DateTime(2026, 5, 1, 9, 0, 0) }

    let record = { toNewTombstoneRecord tombstone with Id = 3 }
    Assert.Equal(7, record.SupplierId)
    Assert.Equal("INV-1042", record.Reference)

    match toInvoiceTombstone record with
    | Ok decoded -> Assert.Equal(tombstone, decoded)
    | Error reason -> Assert.Fail($"did not round-trip: {reason}")

[<Fact; Trait("Level", "Contract")>]
let ``toStoredScanWindow maps id and days`` () =
    match toStoredScanWindow { Id = 4; Days = 90 } with
    | Ok window ->
        Assert.Equal("4", ScanWindowId.value window.Id)
        Assert.Equal(90, ScanWindowDays.value window.Days)
    | Error reason -> Assert.Fail(reason)
