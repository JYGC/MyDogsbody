/// The suppliers adapter: the functions that satisfy the domain's LoadSuppliers, SaveSupplier,
/// UpdateSupplier and DeleteSupplier dependency types.
///
/// Outer ring, so the shape is the established one - dependencies first, input last,
/// Result<'T, MyDogsbodyException> out, written with handleError. The domain error types are not
/// named here; translating between the two is the composition root's job and happens in
/// SupplierApiFactory, nowhere else.
///
/// Dapper.FSharp's SQLite extension methods (SelectAsync/InsertAsync/UpdateAsync/DeleteAsync) are
/// async-only - runSync bridges to this project's synchronous outer-ring shape the same way every
/// other store function in the codebase returns a plain Result rather than an Async<Result<_>>.
module MyDogsbody.Database.SupplierStore

open System
open Microsoft.Data.Sqlite
open Dapper
open Dapper.FSharp.SQLite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Database.Models

let private runSync (task: System.Threading.Tasks.Task<'T>) : 'T =
    task |> Async.AwaitTask |> Async.RunSynchronously

/// Inserts one supplier row via plain Dapper rather than Dapper.FSharp's insert CE.
///
/// Dapper.FSharp's excludeColumn/includeColumn custom operations rewrite their selector through
/// the CE's for-loop binding, which insert{} has none of - there is no bound row variable to
/// rewrite against, so the exclusion never resolves to a usable expression. Raw parameterised SQL
/// sidesteps that, and folding the SELECT last_insert_rowid() into the same command text means it
/// runs in the same connection open/close cycle as the INSERT, which is what makes the returned
/// id reliable regardless of whether the caller's connection was already open.
let private insertSupplierRow
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    (record: SupplierRecord)
    : int =
    connection.ExecuteScalarAsync<int64>(
        "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES (@Name, @PaymentTermDays);
         SELECT last_insert_rowid();",
        {| Name = record.Name; PaymentTermDays = record.PaymentTermDays |},
        transaction
    )
    |> runSync
    |> int

let private insertMatcherRow
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    (record: SupplierMatcherRecord)
    : unit =
    connection.ExecuteAsync(
        "INSERT INTO SupplierMatchers (SupplierId, Kind, Value) VALUES (@SupplierId, @Kind, @Value);",
        {| SupplierId = record.SupplierId; Kind = record.Kind; Value = record.Value |},
        transaction
    )
    |> runSync
    |> ignore

/// A row that cannot be mapped back is a data-integrity failure, not something a user did, so it
/// is raised and caught like any other unexpected failure rather than returned as a value.
let private mapOrRaise (mapResult: Result<StoredSupplier, string>) =
    match mapResult with
    | Ok stored -> stored
    | Error reason -> raise (InvalidOperationException $"Stored supplier is unusable: {reason}")

/// Runs `work` with the connection open and inside a transaction, committing on success. Any
/// exception - including one raised deliberately by mapOrRaise - unwinds without a Commit, so the
/// transaction's own Dispose rolls it back: a write in the middle of a multi-statement sequence
/// (a supplier row plus its matcher rows) never survives a later statement's failure.
let private inTransaction (connection: SqliteConnection) (work: SqliteTransaction -> 'T) : 'T =
    connection.Open()

    try
        use transaction = connection.BeginTransaction()
        let result = work transaction
        transaction.Commit()
        result
    finally
        connection.Close()

let getAll
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getSuppliers: unit -> QuerySource<SupplierRecord>)
    (getSupplierMatchers: unit -> QuerySource<SupplierMatcherRecord>)
    ()
    : Result<StoredSupplier list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.SupplierStore.getAll

    handleError {
        try
            let connection = getConnection ()

            let supplierRows =
                select {
                    for s in getSuppliers () do
                    selectAll
                }
                |> connection.SelectAsync<SupplierRecord>
                |> runSync
                |> Seq.toList

            // One query for every matcher, grouped in memory, rather than one query per supplier -
            // getAll runs on every page load and after every write.
            let matchersBySupplierId =
                select {
                    for m in getSupplierMatchers () do
                    selectAll
                }
                |> connection.SelectAsync<SupplierMatcherRecord>
                |> runSync
                |> Seq.toList
                |> List.groupBy (fun m -> m.SupplierId)
                |> Map.ofList

            return
                supplierRows
                |> List.map (fun row ->
                    let matchers = matchersBySupplierId |> Map.tryFind row.Id |> Option.defaultValue []
                    SupplierRecordMappers.toStoredSupplier row matchers |> mapOrRaise)
        with ex ->
            return! MyDogsbodyException(action, "Failed to retrieve all suppliers.", ex)
    }

let insertOne
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getSuppliers: unit -> QuerySource<SupplierRecord>)
    (getSupplierMatchers: unit -> QuerySource<SupplierMatcherRecord>)
    (supplier: ValidSupplier)
    : Result<StoredSupplier, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.SupplierStore.insertOne

    handleError {
        try
            let connection = getConnection ()
            let newRecord = SupplierRecordMappers.toNewSupplierRecord supplier

            let insertedId =
                inTransaction connection (fun transaction ->
                    let insertedId = insertSupplierRow connection transaction newRecord

                    // List.iter, not a CE `for` loop: HandleErrorBuilder defines no Combine, so a
                    // `for` here could not be sequenced with the `return` that follows it.
                    supplier.Matchers
                    |> List.iter (fun matcher ->
                        SupplierRecordMappers.toNewMatcherRecord insertedId matcher
                        |> insertMatcherRow connection transaction)

                    insertedId)

            // Every field is already known from the input plus the id the insert assigned - no
            // need to read back what was just written.
            return
                SupplierRecordMappers.toStoredSupplier
                    { newRecord with Id = insertedId }
                    (supplier.Matchers |> List.map (SupplierRecordMappers.toNewMatcherRecord insertedId))
                |> mapOrRaise
        with ex ->
            return! MyDogsbodyException(action, "Failed to insert new supplier.", ex)
    }

/// Ok None means no row carried that identifier. Reporting it rather than silently succeeding is
/// what lets EditSupplierWorkflow decide that absence is SupplierNotFound.
let updateOne
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getSuppliers: unit -> QuerySource<SupplierRecord>)
    (getSupplierMatchers: unit -> QuerySource<SupplierMatcherRecord>)
    (edit: ValidSupplierEdit)
    : Result<StoredSupplier option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.SupplierStore.updateOne

    handleError {
        try
            let connection = getConnection ()
            let rowId = SupplierRecordMappers.toRowId edit.Id

            let existing =
                select {
                    for s in getSuppliers () do
                    where (s.Id = rowId)
                }
                |> connection.SelectAsync<SupplierRecord>
                |> runSync
                |> Seq.tryHead

            match existing with
            | None -> return None
            | Some _ ->
                inTransaction connection (fun transaction ->
                    update {
                        for s in getSuppliers () do
                        setColumn s.Name (SupplierName.value edit.Name)
                        setColumn s.PaymentTermDays (PaymentTermDays.value edit.PaymentTermDays)
                        where (s.Id = rowId)
                    }
                    |> fun query -> connection.UpdateAsync(query, transaction)
                    |> runSync
                    |> ignore

                    // The matcher set is replaced, not merged - every existing row is removed and
                    // the submitted list inserted fresh.
                    delete {
                        for m in getSupplierMatchers () do
                        where (m.SupplierId = rowId)
                    }
                    |> fun query -> connection.DeleteAsync(query, transaction)
                    |> runSync
                    |> ignore

                    edit.Matchers
                    |> List.iter (fun matcher ->
                        SupplierRecordMappers.toNewMatcherRecord rowId matcher
                        |> insertMatcherRow connection transaction))

                // Every field is already known from the input plus the row id already confirmed
                // to exist above - no need to read back what was just written.
                let updatedRecord =
                    {
                        Id = rowId
                        Name = SupplierName.value edit.Name
                        PaymentTermDays = PaymentTermDays.value edit.PaymentTermDays
                    }

                return
                    SupplierRecordMappers.toStoredSupplier
                        updatedRecord
                        (edit.Matchers |> List.map (SupplierRecordMappers.toNewMatcherRecord rowId))
                    |> mapOrRaise
                    |> Some
        with ex ->
            return! MyDogsbodyException(action, "Failed to update existing supplier.", ex)
    }

/// True when a row carried that identifier and was removed; false when it did not. Matchers are
/// removed by the database's own cascade (migration ...0002), which is why this needs no
/// getSupplierMatchers of its own.
let deleteOne
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getSuppliers: unit -> QuerySource<SupplierRecord>)
    (id: SupplierId)
    : Result<bool, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.SupplierStore.deleteOne

    handleError {
        try
            let connection = getConnection ()
            let rowId = SupplierRecordMappers.toRowId id

            let existing =
                select {
                    for s in getSuppliers () do
                    where (s.Id = rowId)
                }
                |> connection.SelectAsync<SupplierRecord>
                |> runSync
                |> Seq.tryHead

            match existing with
            | None -> return false
            | Some _ ->
                delete {
                    for s in getSuppliers () do
                    where (s.Id = rowId)
                }
                |> connection.DeleteAsync
                |> runSync
                |> ignore

                return true
        with ex ->
            return! MyDogsbodyException(action, "Failed to delete existing supplier.", ex)
    }
