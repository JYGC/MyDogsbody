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
let private insertSupplierRow (connection: SqliteConnection) (record: SupplierRecord) : int =
    connection.ExecuteScalarAsync<int64>(
        "INSERT INTO Suppliers (Name, PaymentTermDays) VALUES (@Name, @PaymentTermDays);
         SELECT last_insert_rowid();",
        {| Name = record.Name; PaymentTermDays = record.PaymentTermDays |}
    )
    |> runSync
    |> int

let private insertMatcherRow (connection: SqliteConnection) (record: SupplierMatcherRecord) : unit =
    connection.ExecuteAsync(
        "INSERT INTO SupplierMatchers (SupplierId, Kind, Value) VALUES (@SupplierId, @Kind, @Value);",
        {| SupplierId = record.SupplierId; Kind = record.Kind; Value = record.Value |}
    )
    |> runSync
    |> ignore

/// A row that cannot be mapped back is a data-integrity failure, not something a user did, so it
/// is raised and caught like any other unexpected failure rather than returned as a value.
let private mapOrRaise (mapResult: Result<StoredSupplier, string>) =
    match mapResult with
    | Ok stored -> stored
    | Error reason -> raise (InvalidOperationException $"Stored supplier is unusable: {reason}")

let private matchersFor
    (connection: SqliteConnection)
    (getSupplierMatchers: unit -> QuerySource<SupplierMatcherRecord>)
    (supplierId: int)
    : SupplierMatcherRecord list =
    select {
        for m in getSupplierMatchers () do
        where (m.SupplierId = supplierId)
    }
    |> connection.SelectAsync<SupplierMatcherRecord>
    |> runSync
    |> Seq.toList

let private toStored
    (connection: SqliteConnection)
    (getSupplierMatchers: unit -> QuerySource<SupplierMatcherRecord>)
    (row: SupplierRecord)
    : StoredSupplier =
    matchersFor connection getSupplierMatchers row.Id
    |> SupplierRecordMappers.toStoredSupplier row
    |> mapOrRaise

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

            return supplierRows |> List.map (toStored connection getSupplierMatchers)
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
            let insertedId = insertSupplierRow connection newRecord

            // List.iter, not a CE `for` loop: HandleErrorBuilder defines no Combine, so a `for`
            // here could not be sequenced with the `return` that follows it.
            supplier.Matchers
            |> List.iter (fun matcher ->
                SupplierRecordMappers.toNewMatcherRecord insertedId matcher
                |> insertMatcherRow connection)

            let insertedRow =
                select {
                    for s in getSuppliers () do
                    where (s.Id = insertedId)
                }
                |> connection.SelectAsync<SupplierRecord>
                |> runSync
                |> Seq.exactlyOne

            return toStored connection getSupplierMatchers insertedRow
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
                update {
                    for s in getSuppliers () do
                    setColumn s.Name (SupplierName.value edit.Name)
                    setColumn s.PaymentTermDays (PaymentTermDays.value edit.PaymentTermDays)
                    where (s.Id = rowId)
                }
                |> connection.UpdateAsync
                |> runSync
                |> ignore

                // The matcher set is replaced, not merged - every existing row is removed and
                // the submitted list inserted fresh.
                delete {
                    for m in getSupplierMatchers () do
                    where (m.SupplierId = rowId)
                }
                |> connection.DeleteAsync
                |> runSync
                |> ignore

                edit.Matchers
                |> List.iter (fun matcher ->
                    SupplierRecordMappers.toNewMatcherRecord rowId matcher
                    |> insertMatcherRow connection)

                let updatedRow =
                    select {
                        for s in getSuppliers () do
                        where (s.Id = rowId)
                    }
                    |> connection.SelectAsync<SupplierRecord>
                    |> runSync
                    |> Seq.exactlyOne

                return Some (toStored connection getSupplierMatchers updatedRow)
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
