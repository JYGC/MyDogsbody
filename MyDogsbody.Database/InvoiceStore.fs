/// The ledger adapter: the functions that satisfy the domain's LoadInvoices, UpsertInvoice,
/// DeleteInvoice, tombstone and scan-problem dependency types.
///
/// Outer ring - dependencies first, input last, Result<'T, MyDogsbodyException> out, written
/// with handleError. Domain error types are not named here; the composition root translates.
///
/// Reads go through Dapper.FSharp's select {}; writes are raw parameterised SQL (the split-column
/// / insert-CE friction CLAUDE-project.md records), with SELECT last_insert_rowid() folded into
/// the same command so the assigned id is reliable regardless of the connection's open state.
module MyDogsbody.Database.InvoiceStore

open System
open System.Globalization
open Microsoft.Data.Sqlite
open Dapper
open Dapper.FSharp.SQLite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database.Models

let private runSync (task: System.Threading.Tasks.Task<'T>) : 'T =
    task |> Async.AwaitTask |> Async.RunSynchronously

let private invariant = CultureInfo.InvariantCulture

/// A row that cannot be mapped back is a data-integrity failure, raised and caught like any
/// other unexpected adapter failure (SupplierStore.mapOrRaise is the same idea).
let private orRaise (what: string) (result: Result<'T, string>) : 'T =
    match result with
    | Ok value -> value
    | Error reason -> raise (InvalidOperationException $"Stored {what} is unusable: {reason}")

let private withConnection (connection: SqliteConnection) (work: unit -> 'T) : 'T =
    connection.Open()
    try work () finally connection.Close()

let private inTransaction (connection: SqliteConnection) (work: SqliteTransaction -> 'T) : 'T =
    connection.Open()
    try
        use transaction = connection.BeginTransaction()
        let result = work transaction
        transaction.Commit()
        result
    finally
        connection.Close()

/// SQLITE_CONSTRAINT_FOREIGNKEY - the extended result code SQLite reports for a foreign-key
/// violation. (The primary code, 19, is SQLITE_CONSTRAINT and covers unique and check violations
/// too, so it is the extended one that identifies this.)
[<Literal>]
let private ForeignKeyViolation = 787

/// Whether a failed write failed because the supplier row it referenced is not there any more.
///
/// `Invoices` and `InvoiceTombstones` each declare exactly one foreign key - `SupplierId` - so a
/// foreign-key violation on a write to either says precisely that and nothing else. The
/// composition root turns it into the domain's `SupplierGone`, which
/// `ScanForInvoicesWorkflow.step` records as that one message's problem and carries on past;
/// requirements.md asks for exactly that - "WHEN a scan finds an invoice whose supplier has since
/// been deleted THE SYSTEM SHALL report it as a problem rather than storing an invoice with no
/// supplier."
///
/// Without it the violation arrived as an undifferentiated `InvoiceStoreFailed`, which the same
/// `step` treats as FATAL: one supplier deleted from the suppliers page while a scan was running
/// (measured at ~60 s, so the window is wide open, and the page stays reachable throughout) ended
/// the whole run with "Failed to store invoice.", discarded every problem the scan had gathered,
/// and reset the account's watermarks. The `SupplierGone` branch and its unit test existed from the
/// start; nothing in production could reach them.
///
/// Kept here rather than at the composition root because it is SQLite knowledge: swapping the
/// store swaps this with it, and the factory's one line stays as it is.
///
/// The chain is walked rather than probed at a fixed depth - `runSync` (Async.AwaitTask) wraps the
/// SqliteException in an AggregateException, so the real one sits two levels down today, and
/// nothing should depend on it staying there.
let isMissingSupplier (ex: MyDogsbodyException) : bool =
    let rec search (candidate: exn) : bool =
        match candidate with
        | null -> false
        | :? SqliteException as sqlite when sqlite.SqliteExtendedErrorCode = ForeignKeyViolation -> true
        | :? AggregateException as aggregate -> aggregate.InnerExceptions |> Seq.exists search
        | other -> search other.InnerException

    search ex

// ---------------- invoices ----------------

let getInvoices
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoicesQ: unit -> QuerySource<InvoiceRecord>)
    (cutoff: ScanCutoff option)
    : Result<StoredInvoice list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.getInvoices

    handleError {
        try
            let connection = getConnection ()

            let rows =
                withConnection connection (fun () ->
                    select {
                        for i in getInvoicesQ () do
                        selectAll
                    }
                    |> connection.SelectAsync<InvoiceRecord>
                    |> runSync
                    |> Seq.toList)

            let stored = rows |> List.map (InvoiceRecordMappers.toStoredInvoice >> orRaise "invoice")

            return
                match cutoff with
                | None -> stored
                | Some c ->
                    let from = ScanCutoff.value c
                    stored |> List.filter (fun invoice -> invoice.Invoice.MessageReceivedAt >= from)
        with ex ->
            return! MyDogsbodyException(action, "Failed to retrieve invoices.", ex)
    }

let upsertInvoice
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getCurrentTime: unit -> DateTime)
    (invoice: ValidInvoice)
    : Result<StoredInvoice, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.upsertInvoice

    handleError {
        try
            let connection = getConnection ()
            let record = InvoiceRecordMappers.toNewInvoiceRecord invoice
            let scannedAt = (getCurrentTime ()).ToString("o", invariant)

            let toObj (v: string option) = match v with Some s -> box s | None -> box DBNull.Value

            let parameters: obj =
                {| SupplierId = record.SupplierId
                   TemplateId = record.TemplateId
                   Reference = record.Reference
                   Amount = record.Amount
                   Currency = record.Currency
                   IssueDate = toObj record.IssueDate
                   DueDate = toObj record.DueDate
                   SourceMessageId = record.SourceMessageId
                   MessageReceivedAt = record.MessageReceivedAt
                   ScannedAt = scannedAt |}

            let assignedId =
                inTransaction connection (fun transaction ->
                    // ON CONFLICT on the (SupplierId, Reference) unique index turns this into an
                    // upsert on the natural key (Q5.8). RETURNING Id gives back the row's id
                    // whether it was inserted or updated.
                    connection.ExecuteScalarAsync<int64>(
                        "INSERT INTO Invoices
                            (SupplierId, TemplateId, Reference, Amount, Currency, IssueDate, DueDate,
                             SourceMessageId, MessageReceivedAt, ScannedAt)
                         VALUES
                            (@SupplierId, @TemplateId, @Reference, @Amount, @Currency, @IssueDate, @DueDate,
                             @SourceMessageId, @MessageReceivedAt, @ScannedAt)
                         ON CONFLICT (SupplierId, Reference) DO UPDATE SET
                            TemplateId = excluded.TemplateId,
                            Amount = excluded.Amount,
                            Currency = excluded.Currency,
                            IssueDate = excluded.IssueDate,
                            DueDate = excluded.DueDate,
                            SourceMessageId = excluded.SourceMessageId,
                            MessageReceivedAt = excluded.MessageReceivedAt,
                            ScannedAt = excluded.ScannedAt
                         RETURNING Id;",
                        parameters,
                        transaction
                    )
                    |> runSync)

            return
                { record with Id = int assignedId; ScannedAt = scannedAt }
                |> InvoiceRecordMappers.toStoredInvoice
                |> orRaise "invoice"
        with ex ->
            return! MyDogsbodyException(action, "Failed to store invoice.", ex)
    }

let deleteInvoice
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoicesQ: unit -> QuerySource<InvoiceRecord>)
    (invoiceId: InvoiceId)
    : Result<StoredInvoice option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.deleteInvoice

    handleError {
        try
            let connection = getConnection ()
            let rowId = InvoiceRecordMappers.toRowId invoiceId

            let deleted =
                inTransaction connection (fun transaction ->
                    let existing =
                        connection.QueryAsync<InvoiceRecord>(
                            "SELECT * FROM Invoices WHERE Id = @Id;",
                            {| Id = rowId |},
                            transaction
                        )
                        |> runSync
                        |> Seq.tryHead

                    match existing with
                    | None -> None
                    | Some row ->
                        connection.ExecuteAsync("DELETE FROM Invoices WHERE Id = @Id;", {| Id = rowId |}, transaction)
                        |> runSync
                        |> ignore

                        Some row)

            return deleted |> Option.map (InvoiceRecordMappers.toStoredInvoice >> orRaise "invoice")
        with ex ->
            return! MyDogsbodyException(action, "Failed to delete invoice.", ex)
    }

// ---------------- tombstones ----------------

let getTombstones
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getTombstonesQ: unit -> QuerySource<InvoiceTombstoneRecord>)
    ()
    : Result<InvoiceTombstone list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.getTombstones

    handleError {
        try
            let connection = getConnection ()

            let rows =
                withConnection connection (fun () ->
                    select {
                        for t in getTombstonesQ () do
                        selectAll
                    }
                    |> connection.SelectAsync<InvoiceTombstoneRecord>
                    |> runSync
                    |> Seq.toList)

            return rows |> List.map (InvoiceRecordMappers.toInvoiceTombstone >> orRaise "tombstone")
        with ex ->
            return! MyDogsbodyException(action, "Failed to retrieve invoice tombstones.", ex)
    }

let saveTombstone
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (tombstone: InvoiceTombstone)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.saveTombstone

    handleError {
        try
            let connection = getConnection ()
            let record = InvoiceRecordMappers.toNewTombstoneRecord tombstone

            withConnection connection (fun () ->
                // Idempotent: a key already tombstoned is left as it is.
                connection.ExecuteAsync(
                    "INSERT INTO InvoiceTombstones (SupplierId, Reference, DeletedAt)
                     VALUES (@SupplierId, @Reference, @DeletedAt)
                     ON CONFLICT (SupplierId, Reference) DO NOTHING;",
                    {| SupplierId = record.SupplierId; Reference = record.Reference; DeletedAt = record.DeletedAt |}
                )
                |> runSync
                |> ignore)

            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to write invoice tombstone.", ex)
    }

let removeTombstone
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (supplierId: SupplierId)
    (reference: InvoiceReference)
    : Result<bool, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.removeTombstone

    handleError {
        try
            let connection = getConnection ()

            let affected =
                withConnection connection (fun () ->
                    connection.ExecuteAsync(
                        "DELETE FROM InvoiceTombstones WHERE SupplierId = @SupplierId AND Reference = @Reference;",
                        {| SupplierId = InvoiceRecordMappers.supplierRowId supplierId
                           Reference = InvoiceReference.value reference |}
                    )
                    |> runSync)

            return affected > 0
        with ex ->
            return! MyDogsbodyException(action, "Failed to remove invoice tombstone.", ex)
    }

// ---------------- scan problems ----------------

let getScanProblems
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getScanProblemsQ: unit -> QuerySource<ScanProblemRecord>)
    ()
    : Result<ScanProblem list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.getScanProblems

    handleError {
        try
            let connection = getConnection ()

            let rows =
                withConnection connection (fun () ->
                    select {
                        for p in getScanProblemsQ () do
                        selectAll
                    }
                    |> connection.SelectAsync<ScanProblemRecord>
                    |> runSync
                    |> Seq.toList)

            return rows |> List.map (InvoiceRecordMappers.toScanProblem >> orRaise "scan problem")
        with ex ->
            return! MyDogsbodyException(action, "Failed to retrieve scan problems.", ex)
    }

let private toObj (value: string option) : obj =
    match value with
    | Some s -> box s
    | None -> box DBNull.Value

let private deleteProblemsFor (connection: SqliteConnection) (transaction: SqliteTransaction) (messageId: string) : unit =
    connection.ExecuteAsync(
        "DELETE FROM ScanProblems WHERE SourceMessageId = @SourceMessageId;",
        {| SourceMessageId = messageId |},
        transaction
    )
    |> runSync
    |> ignore

let private insertProblem (connection: SqliteConnection) (transaction: SqliteTransaction) (record: ScanProblemRecord) : unit =
    connection.ExecuteAsync(
        "INSERT INTO ScanProblems
            (SourceMessageId, SupplierId, Sender, Subject, ReceivedAt, Cause, Detail, RecordedAt)
         VALUES
            (@SourceMessageId, @SupplierId, @Sender, @Subject, @ReceivedAt, @Cause, @Detail, @RecordedAt);",
        {| SourceMessageId = record.SourceMessageId
           SupplierId = (match record.SupplierId with Some s -> box s | None -> box DBNull.Value)
           Sender = record.Sender
           Subject = record.Subject
           ReceivedAt = record.ReceivedAt
           Cause = record.Cause
           Detail = toObj record.Detail
           RecordedAt = record.RecordedAt |},
        transaction
    )
    |> runSync
    |> ignore

let saveScanProblems
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (problems: ScanProblem list)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.saveScanProblems

    handleError {
        try
            let connection = getConnection ()

            let write () =
                inTransaction connection (fun transaction ->
                    problems
                    |> List.iter (fun problem ->
                        let record = InvoiceRecordMappers.toNewScanProblemRecord problem
                        // Replace, not append: the message's previous problem row is removed
                        // first so a rescan does not duplicate it.
                        deleteProblemsFor connection transaction record.SourceMessageId
                        insertProblem connection transaction record))

            let _ = (if List.isEmpty problems then () else write ())
            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to save scan problems.", ex)
    }

let clearScanProblems
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (sourceMessageIds: SourceMessageId list)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.InvoiceStore.clearScanProblems

    handleError {
        try
            let connection = getConnection ()

            let clear () =
                inTransaction connection (fun transaction ->
                    sourceMessageIds
                    |> List.iter (fun messageId ->
                        deleteProblemsFor connection transaction (SourceMessageId.value messageId)))

            let _ = (if List.isEmpty sourceMessageIds then () else clear ())
            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to clear scan problems.", ex)
    }
