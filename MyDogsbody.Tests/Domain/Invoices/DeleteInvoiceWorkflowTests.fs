module MyDogsbody.Tests.Domain.Invoices.DeleteInvoiceWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private storedInvoice: StoredInvoice =
    { Id = InvoiceId.create "row-1" |> orFail
      ScannedAt = DateTime(2026, 6, 1)
      Invoice =
        { SupplierId = SupplierId.create "acme" |> orFail
          TemplateId = TemplateId.create "t1" |> orFail
          SourceMessageId = SourceMessageId.create "m1" |> orFail
          Reference = InvoiceReference.create "INV-1042" |> orFail
          Amount = Money.create 10m "AUD" |> orFail
          IssueDate = None
          DueDate = None
          MessageReceivedAt = DateTime(2026, 6, 1) } }

let private clock: GetCurrentTime = fun () -> DateTime(2026, 7, 1, 9, 0, 0)

[<Fact; Trait("Level", "Unit")>]
let ``delete removes the row and then writes a tombstone on the natural key`` () =
    let mutable deletedId = None
    let mutable tombstone: InvoiceTombstone option = None

    let deleteFromLedger: DeleteInvoice =
        fun id ->
            deletedId <- Some id
            Ok(Some storedInvoice)

    let saveTombstone: SaveTombstone =
        fun t ->
            tombstone <- Some t
            Ok()

    match DeleteInvoiceWorkflow.deleteInvoice deleteFromLedger saveTombstone clock "row-1" with
    | Ok() ->
        Assert.Equal(Some(InvoiceId.create "row-1" |> orFail), deletedId)

        match tombstone with
        | Some t ->
            Assert.Equal("acme", SupplierId.value t.SupplierId)
            Assert.Equal("INV-1042", InvoiceReference.value t.Reference)
            Assert.Equal(DateTime(2026, 7, 1, 9, 0, 0), t.DeletedAt)
        | None -> Assert.Fail("no tombstone written")
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``deleting an invoice that is already gone is InvoiceNotFound with no tombstone written`` () =
    let mutable tombstoneWritten = false

    let deleteFromLedger: DeleteInvoice = fun _ -> Ok None

    let saveTombstone: SaveTombstone =
        fun _ ->
            tombstoneWritten <- true
            Ok()

    match DeleteInvoiceWorkflow.deleteInvoice deleteFromLedger saveTombstone clock "row-1" with
    | Error InvoiceNotFound -> Assert.False(tombstoneWritten, "no tombstone must be written")
    | other -> Assert.Fail($"Expected Error InvoiceNotFound, got {other}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``deleting with a blank id is InvoiceNotFound`` (raw: string) =
    let called = ref false

    let deleteFromLedger: DeleteInvoice =
        fun _ ->
            called.Value <- true
            Ok None

    match DeleteInvoiceWorkflow.deleteInvoice deleteFromLedger (fun _ -> Ok()) clock raw with
    | Error InvoiceNotFound -> Assert.False(called.Value, "the store must not be reached")
    | other -> Assert.Fail($"Expected Error InvoiceNotFound, got {other}")

// ---- undelete ----

[<Fact; Trait("Level", "Unit")>]
let ``undelete removes the tombstone for the key`` () =
    let mutable removedKey = None

    let removeTombstone: RemoveTombstone =
        fun supplierId reference ->
            removedKey <- Some(SupplierId.value supplierId, InvoiceReference.value reference)
            Ok true

    let supplierId = SupplierId.create "acme" |> orFail
    let reference = InvoiceReference.create "INV-1042" |> orFail

    match UndeleteInvoiceWorkflow.undeleteInvoice removeTombstone supplierId reference with
    | Ok() -> Assert.Equal(Some("acme", "INV-1042"), removedKey)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``undeleting a key with no tombstone is reported, not silently ignored`` () =
    let removeTombstone: RemoveTombstone = fun _ _ -> Ok false
    let supplierId = SupplierId.create "acme" |> orFail
    let reference = InvoiceReference.create "INV-nope" |> orFail

    match UndeleteInvoiceWorkflow.undeleteInvoice removeTombstone supplierId reference with
    | Error InvoiceNotFound -> ()
    | other -> Assert.Fail($"Expected Error InvoiceNotFound, got {other}")
