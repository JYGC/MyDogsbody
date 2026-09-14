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
    use command = seed.CreateCommand()
    command.CommandText <-
        "INSERT INTO Suppliers (Id, Name, PaymentTermDays) VALUES (1, 'Acme', 30);
         INSERT INTO InvoiceTemplates (Id, SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (1, 1, 'T', 'AnyPart', NULL, 0);"
    command.ExecuteNonQuery() |> ignore
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
    withLedger (fun context ->
        InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock (invoice "INV-5" 100m) |> orFail |> ignore
        let updated = InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock (invoice "INV-5" 250m) |> orFail

        Assert.Equal(250m, Money.amount updated.Invoice.Amount)

        let all = InvoiceStore.getInvoices handleError context.GetDatabaseConnection context.GetInvoices None |> orFail
        Assert.Equal(1, List.length all))

[<Fact; Trait("Level", "Integration")>]
let ``two invoices from one message are both stored - the source message id is not the key`` () =
    withLedger (fun context ->
        InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock (invoice "INV-A" 10m) |> orFail |> ignore
        InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock (invoice "INV-B" 20m) |> orFail |> ignore

        let all = InvoiceStore.getInvoices handleError context.GetDatabaseConnection context.GetInvoices None |> orFail
        Assert.Equal(2, List.length all)
        Assert.All(all, fun storedInvoice -> Assert.Equal("msg-1", SourceMessageId.value storedInvoice.Invoice.SourceMessageId)))

[<Fact; Trait("Level", "Integration")>]
let ``getInvoices filters out invoices whose message arrived before the cutoff`` () =
    withLedger (fun context ->
        InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock ({ invoice "OLD" 10m with MessageReceivedAt = DateTime(2026, 1, 1) }) |> orFail |> ignore
        InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock ({ invoice "NEW" 20m with MessageReceivedAt = DateTime(2026, 5, 30) }) |> orFail |> ignore

        let cutoff = ScanCutoff.ofStartOfDay (DateTime(2026, 5, 1))
        let inWindow = InvoiceStore.getInvoices handleError context.GetDatabaseConnection context.GetInvoices (Some cutoff) |> orFail

        Assert.Equal<string list>([ "NEW" ], inWindow |> List.map (fun invoiceMatch -> InvoiceReference.value invoiceMatch.Invoice.Reference))
        // and the old one is still in the ledger - narrowing hides, it does not delete
        Assert.Equal(2, InvoiceStore.getInvoices handleError context.GetDatabaseConnection context.GetInvoices None |> orFail |> List.length))

[<Fact; Trait("Level", "Integration")>]
let ``delete returns the row it removed and then it is gone`` () =
    withLedger (fun context ->
        let stored = InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock (invoice "INV-1" 10m) |> orFail

        match InvoiceStore.deleteInvoice handleError context.GetDatabaseConnection context.GetInvoices stored.Id |> orFail with
        | Some removed -> Assert.Equal("INV-1", InvoiceReference.value removed.Invoice.Reference)
        | None -> Assert.Fail("expected the deleted row back")

        Assert.Empty(InvoiceStore.getInvoices handleError context.GetDatabaseConnection context.GetInvoices None |> orFail))

[<Fact; Trait("Level", "Integration")>]
let ``deleting an invoice that is not there returns None`` () =
    withLedger (fun context ->
        let missing = InvoiceId.create "999" |> orFail
        Assert.Equal(None, InvoiceStore.deleteInvoice handleError context.GetDatabaseConnection context.GetInvoices missing |> orFail))

[<Fact; Trait("Level", "Integration")>]
let ``tombstones round-trip and remove returns whether a row was there`` () =
    withLedger (fun context ->
        let tombstone: InvoiceTombstone =
            { SupplierId = SupplierId.create "1" |> orFail
              Reference = InvoiceReference.create "INV-7" |> orFail
              DeletedAt = DateTime(2026, 5, 1, 9, 0, 0) }

        InvoiceStore.saveTombstone handleError context.GetDatabaseConnection tombstone |> orFail
        InvoiceStore.saveTombstone handleError context.GetDatabaseConnection tombstone |> orFail // idempotent

        let loaded = InvoiceStore.getTombstones handleError context.GetDatabaseConnection context.GetInvoiceTombstones () |> orFail
        Assert.Equal(1, List.length loaded)
        Assert.Equal(tombstone, List.head loaded)

        Assert.True(InvoiceStore.removeTombstone handleError context.GetDatabaseConnection tombstone.SupplierId tombstone.Reference |> orFail)
        Assert.False(InvoiceStore.removeTombstone handleError context.GetDatabaseConnection tombstone.SupplierId tombstone.Reference |> orFail))

[<Fact; Trait("Level", "Integration")>]
let ``scan problems are written, replaced per message, and cleared`` () =
    withLedger (fun context ->
        let problem (messageId: string) (cause: ScanProblemCause) : ScanProblem =
            { SourceMessageId = SourceMessageId.create messageId |> orFail
              Sender = "billing@acme.test"
              Subject = "Invoice"
              ReceivedAt = DateTime(2026, 5, 20)
              Cause = cause
              RecordedAt = DateTime(2026, 6, 1) }

        InvoiceStore.saveScanProblems handleError context.GetDatabaseConnection [ problem "m1" NoSupplierMatched ] |> orFail
        // re-save for the same message with a different cause - replaces, does not duplicate
        InvoiceStore.saveScanProblems handleError context.GetDatabaseConnection [ problem "m1" (NoTemplateMatched(SupplierId.create "1" |> orFail)) ] |> orFail

        let after = InvoiceStore.getScanProblems handleError context.GetDatabaseConnection context.GetScanProblems () |> orFail
        Assert.Equal(1, List.length after)
        Assert.Equal(NoTemplateMatched(SupplierId.create "1" |> orFail), (List.head after).Cause)

        InvoiceStore.clearScanProblems handleError context.GetDatabaseConnection [ SourceMessageId.create "m1" |> orFail ] |> orFail
        Assert.Empty(InvoiceStore.getScanProblems handleError context.GetDatabaseConnection context.GetScanProblems () |> orFail))

[<Fact; Trait("Level", "Integration")>]
let ``an upsert naming a supplier that is not there is refused, and isMissingSupplier says so`` () =
    withLedger (fun context ->
        // supplier 2 was never inserted - the Invoices foreign key refuses the write
        let orphan = { invoice "INV-ORPHAN" 10m with SupplierId = SupplierId.create "2" |> orFail }

        match InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock orphan with
        | Error caughtException ->
            Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceStore.upsertInvoice, caughtException.ActionName)
            Assert.Equal("Failed to store invoice.", caughtException.Message)
            Assert.True(InvoiceStore.isMissingSupplier caughtException, "the foreign-key violation should be identified as a missing supplier")
        | Ok _ -> Assert.Fail("expected the write to be refused")

        Assert.Empty(InvoiceStore.getInvoices handleError context.GetDatabaseConnection context.GetInvoices None |> orFail))

[<Fact; Trait("Level", "Integration")>]
let ``isMissingSupplier is false for a store failure that is not a foreign-key violation`` () =
    withLedger (fun context ->
        // a real infrastructure failure: the table is gone, SQLite error 1, not a constraint
        let connection = context.GetDatabaseConnection()
        connection.Open()
        use drop = connection.CreateCommand()
        drop.CommandText <- "DROP TABLE Invoices;"
        drop.ExecuteNonQuery() |> ignore
        connection.Close()

        match InvoiceStore.upsertInvoice handleError context.GetDatabaseConnection clock (invoice "INV-1" 10m) with
        | Error caughtException ->
            Assert.False(InvoiceStore.isMissingSupplier caughtException, "a missing table is not a missing supplier")
            Assert.NotNull caughtException.InnerException
        | Ok _ -> Assert.Fail("expected Error"))

[<Fact; Trait("Level", "Unit")>]
let ``isMissingSupplier is false for a failure with no SQLite exception under it at all`` () =
    let boom () : SqliteConnection = raise (InvalidOperationException "connection is down")

    match InvoiceStore.upsertInvoice handleError boom clock (invoice "INV-1" 10m) with
    | Error caughtException -> Assert.False(InvoiceStore.isMissingSupplier caughtException)
    | Ok _ -> Assert.Fail("expected Error")

/// SQLITE_CONSTRAINT_FOREIGNKEY / SQLITE_CONSTRAINT_UNIQUE. Both share primary code 19
/// (SQLITE_CONSTRAINT), which is exactly why isMissingSupplier reads the EXTENDED one - a unique
/// violation on (SupplierId, Reference) is a different fault with a different remedy, and calling
/// it "the supplier is gone" would record the wrong problem and let a broken write pass as
/// non-fatal.
let private foreignKeyViolation () =
    SqliteException("FOREIGN KEY constraint failed", 19, 787)

let private uniqueViolation () =
    SqliteException("UNIQUE constraint failed", 19, 2067)

/// The chain walk, with no database: the store's own error path is asserted by the two
/// integration tests above, but the walking itself has to hold for any depth and any wrapper -
/// runSync's Async.AwaitTask puts the real exception inside an AggregateException today and
/// nothing should depend on it staying exactly there.
[<Theory; Trait("Level", "Unit")>]
[<InlineData("directly under the MyDogsbodyException", 0)>]
[<InlineData("inside an AggregateException - the runSync shape", 1)>]
[<InlineData("two wrappers down", 2)>]
let ``isMissingSupplier finds a foreign-key violation at any depth`` (_shape: string) (depth: int) =
    let wrapped: exn =
        match depth with
        | 0 -> foreignKeyViolation ()
        | 1 -> AggregateException("one", foreignKeyViolation ()) :> exn
        | _ -> AggregateException("outer", AggregateException("inner", foreignKeyViolation ())) :> exn

    let caughtException = MyDogsbodyException(ActionNames.MyDogsbody.Database.InvoiceStore.upsertInvoice, "Failed to store invoice.", wrapped)

    Assert.True(InvoiceStore.isMissingSupplier caughtException)

[<Fact; Trait("Level", "Unit")>]
let ``isMissingSupplier is false for a constraint violation that is not the foreign key`` () =
    let caughtException =
        MyDogsbodyException(
            ActionNames.MyDogsbody.Database.InvoiceStore.upsertInvoice,
            "Failed to store invoice.",
            AggregateException("one", uniqueViolation ())
        )

    Assert.False(InvoiceStore.isMissingSupplier caughtException)

[<Fact; Trait("Level", "Unit")>]
let ``isMissingSupplier sees the violation even when a sibling in the AggregateException does not match`` () =
    let aggregate =
        AggregateException("two", [| uniqueViolation () :> exn; foreignKeyViolation () :> exn |])

    let caughtException =
        MyDogsbodyException(ActionNames.MyDogsbody.Database.InvoiceStore.upsertInvoice, "Failed to store invoice.", aggregate)

    Assert.True(InvoiceStore.isMissingSupplier caughtException)

// ============================ 7.2 Unit - error paths ============================

[<Fact; Trait("Level", "Unit")>]
let ``a store failure reports the declared action, message and preserves the inner exception`` () =
    let boom () : SqliteConnection = raise (InvalidOperationException "connection is down")

    match InvoiceStore.getInvoices handleError boom (fun () -> failwith "unused") None with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceStore.getInvoices, caughtException.ActionName)
        Assert.Equal("Failed to retrieve invoices.", caughtException.Message)
        Assert.IsType<InvalidOperationException>(caughtException.InnerException) |> ignore
    | Ok _ -> Assert.Fail("expected Error")

[<Fact; Trait("Level", "Unit")>]
let ``upsert reports its declared action on failure`` () =
    let boom () : SqliteConnection = raise (InvalidOperationException "nope")

    match InvoiceStore.upsertInvoice handleError boom clock (invoice "INV-1" 10m) with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Database.InvoiceStore.upsertInvoice, caughtException.ActionName)
        Assert.Equal("Failed to store invoice.", caughtException.Message)
        Assert.NotNull caughtException.InnerException
    | Ok _ -> Assert.Fail("expected Error")
