module MyDogsbody.Tests.Database.InvoiceStoreTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database
open MyDogsbody.Database.Migrations

let private handleError = HandleErrorBuilder(fun _ -> ())

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

/// Fresh migrated temp DB with one supplier (id 1) and one template (id 1) already inserted, so
/// the Invoices foreign keys are satisfied.
let private withLedger (test: DatabaseContext -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={path}"
    MigrationSetup.setupMigrations connectionString

    use seed = new SqliteConnection(connectionString)
    seed.Open()
    use cmd = seed.CreateCommand()
    cmd.CommandText <-
        "INSERT INTO Suppliers (Id, Name, PaymentTermDays) VALUES (1, 'Acme', 30);
         INSERT INTO InvoiceTemplates (Id, SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (1, 1, 'T', 'AnyPart', NULL, 0);"
    cmd.ExecuteNonQuery() |> ignore
    seed.Close()

    let context = DatabaseContextSetup.createDatabaseContext path

    try
        test context
    finally
        context.Dispose()
        // Not ClearAllPools() - that is process-global and clears pooled connections other
        // tests are mid-use of. Leave the temp file if the pool still holds a handle; a stray
        // GUID-named file in %TEMP% is cheaper than a cross-test flake.
        try File.Delete path with _ -> ()

let private clock () : DateTime = DateTime(2026, 6, 1, 12, 0, 0)

let private invoice (reference: string) (amount: decimal) : ValidInvoice =
    { SupplierId = SupplierId.create "1" |> orFail
      TemplateId = TemplateId.create "1" |> orFail
      SourceMessageId = SourceMessageId.create "msg-1" |> orFail
      Reference = InvoiceReference.create reference |> orFail
      Amount = Money.create amount "AUD" |> orFail
      IssueDate = None
      DueDate = None
      MessageReceivedAt = DateTime(2026, 5, 20) }

// ============================ 7.2 Integration ============================

[<Fact; Trait("Level", "Integration")>]
let ``upsert on the natural key updates rather than duplicates`` () =
    withLedger (fun ctx ->
        InvoiceStore.upsertInvoice handleError ctx.GetDatabaseConnection clock (invoice "INV-5" 100m) |> orFail |> ignore
        let updated = InvoiceStore.upsertInvoice handleError ctx.GetDatabaseConnection clock (invoice "INV-5" 250m) |> orFail

        Assert.Equal(250m, Money.amount updated.Invoice.Amount)

        let all = InvoiceStore.getInvoices handleError ctx.GetDatabaseConnection ctx.GetInvoices None |> orFail
        Assert.Equal(1, List.length all))

[<Fact; Trait("Level", "Integration")>]
let ``two invoices from one message are both stored - the source message id is not the key`` () =
    withLedger (fun ctx ->
        InvoiceStore.upsertInvoice handleError ctx.GetDatabaseConnection clock (invoice "INV-A" 10m) |> orFail |> ignore
        InvoiceStore.upsertInvoice handleError ctx.GetDatabaseConnection clock (invoice "INV-B" 20m) |> orFail |> ignore

        let all = InvoiceStore.getInvoices handleError ctx.GetDatabaseConnection ctx.GetInvoices None |> orFail
        Assert.Equal(2, List.length all)
        Assert.All(all, fun i -> Assert.Equal("msg-1", SourceMessageId.value i.Invoice.SourceMessageId)))

[<Fact; Trait("Level", "Integration")>]
let ``getInvoices filters out invoices whose message arrived before the cutoff`` () =
    withLedger (fun ctx ->
        InvoiceStore.upsertInvoice handleError ctx.GetDatabaseConnection clock ({ invoice "OLD" 10m with MessageReceivedAt = DateTime(2026, 1, 1) }) |> orFail |> ignore
        InvoiceStore.upsertInvoice handleError ctx.GetDatabaseConnection clock ({ invoice "NEW" 20m with MessageReceivedAt = DateTime(2026, 5, 30) }) |> orFail |> ignore

        let cutoff = ScanCutoff.ofStartOfDay (DateTime(2026, 5, 1))
        let inWindow = InvoiceStore.getInvoices handleError ctx.GetDatabaseConnection ctx.GetInvoices (Some cutoff) |> orFail

        Assert.Equal<string list>([ "NEW" ], inWindow |> List.map (fun i -> InvoiceReference.value i.Invoice.Reference))
        // and the old one is still in the ledger - narrowing hides, it does not delete
        Assert.Equal(2, InvoiceStore.getInvoices handleError ctx.GetDatabaseConnection ctx.GetInvoices None |> orFail |> List.length))

[<Fact; Trait("Level", "Integration")>]
let ``delete returns the row it removed and then it is gone`` () =
    withLedger (fun ctx ->
        let stored = InvoiceStore.upsertInvoice handleError ctx.GetDatabaseConnection clock (invoice "INV-1" 10m) |> orFail

        match InvoiceStore.deleteInvoice handleError ctx.GetDatabaseConnection ctx.GetInvoices stored.Id |> orFail with
        | Some removed -> Assert.Equal("INV-1", InvoiceReference.value removed.Invoice.Reference)
        | None -> Assert.Fail("expected the deleted row back")

        Assert.Empty(InvoiceStore.getInvoices handleError ctx.GetDatabaseConnection ctx.GetInvoices None |> orFail))

[<Fact; Trait("Level", "Integration")>]
let ``deleting an invoice that is not there returns None`` () =
    withLedger (fun ctx ->
        let missing = InvoiceId.create "999" |> orFail
        Assert.Equal(None, InvoiceStore.deleteInvoice handleError ctx.GetDatabaseConnection ctx.GetInvoices missing |> orFail))

[<Fact; Trait("Level", "Integration")>]
let ``tombstones round-trip and remove returns whether a row was there`` () =
    withLedger (fun ctx ->
        let tombstone: InvoiceTombstone =
            { SupplierId = SupplierId.create "1" |> orFail
              Reference = InvoiceReference.create "INV-7" |> orFail
              DeletedAt = DateTime(2026, 5, 1, 9, 0, 0) }

        InvoiceStore.saveTombstone handleError ctx.GetDatabaseConnection tombstone |> orFail
        InvoiceStore.saveTombstone handleError ctx.GetDatabaseConnection tombstone |> orFail // idempotent

        let loaded = InvoiceStore.getTombstones handleError ctx.GetDatabaseConnection ctx.GetInvoiceTombstones () |> orFail
        Assert.Equal(1, List.length loaded)
        Assert.Equal(tombstone, List.head loaded)

        Assert.True(InvoiceStore.removeTombstone handleError ctx.GetDatabaseConnection tombstone.SupplierId tombstone.Reference |> orFail)
        Assert.False(InvoiceStore.removeTombstone handleError ctx.GetDatabaseConnection tombstone.SupplierId tombstone.Reference |> orFail))

[<Fact; Trait("Level", "Integration")>]
let ``scan problems are written, replaced per message, and cleared`` () =
    withLedger (fun ctx ->
        let problem (messageId: string) (cause: ScanProblemCause) : ScanProblem =
            { SourceMessageId = SourceMessageId.create messageId |> orFail
              Sender = "billing@acme.test"
              Subject = "Invoice"
              ReceivedAt = DateTime(2026, 5, 20)
              Cause = cause
              RecordedAt = DateTime(2026, 6, 1) }

        InvoiceStore.saveScanProblems handleError ctx.GetDatabaseConnection [ problem "m1" NoSupplierMatched ] |> orFail
        // re-save for the same message with a different cause - replaces, does not duplicate
        InvoiceStore.saveScanProblems handleError ctx.GetDatabaseConnection [ problem "m1" (NoTemplateMatched(SupplierId.create "1" |> orFail)) ] |> orFail

        let after = InvoiceStore.getScanProblems handleError ctx.GetDatabaseConnection ctx.GetScanProblems () |> orFail
        Assert.Equal(1, List.length after)
        Assert.Equal(NoTemplateMatched(SupplierId.create "1" |> orFail), (List.head after).Cause)

        InvoiceStore.clearScanProblems handleError ctx.GetDatabaseConnection [ SourceMessageId.create "m1" |> orFail ] |> orFail
        Assert.Empty(InvoiceStore.getScanProblems handleError ctx.GetDatabaseConnection ctx.GetScanProblems () |> orFail))

// ============================ 7.2 Unit - error paths ============================

[<Fact; Trait("Level", "Unit")>]
let ``a store failure reports the declared action, message and preserves the inner exception`` () =
    let boom () : SqliteConnection = raise (InvalidOperationException "connection is down")

    match InvoiceStore.getInvoices handleError boom (fun () -> failwith "unused") None with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceStore.getInvoices, ex.ActionName)
        Assert.Equal("Failed to retrieve invoices.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
    | Ok _ -> Assert.Fail("expected Error")

[<Fact; Trait("Level", "Unit")>]
let ``upsert reports its declared action on failure`` () =
    let boom () : SqliteConnection = raise (InvalidOperationException "nope")

    match InvoiceStore.upsertInvoice handleError boom clock (invoice "INV-1" 10m) with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceStore.upsertInvoice, ex.ActionName)
        Assert.Equal("Failed to store invoice.", ex.Message)
        Assert.NotNull ex.InnerException
    | Ok _ -> Assert.Fail("expected Error")
