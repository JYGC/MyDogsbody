/// The InvoiceTemplates adapter: the functions that satisfy the domain's LoadTemplatesForSupplier,
/// SaveTemplate, UpdateTemplate, DeleteTemplate and ReorderTemplates dependency types.
///
/// Outer ring, so the shape is the established one - dependencies first, input last,
/// Result<'T, MyDogsbodyException> out, written with handleError. Copies four shapes directly
/// from SupplierStore.fs rather than re-deriving them: runSync (the Dapper.FSharp async-only
/// bridge), inTransaction, raw parameterised SQL with SELECT last_insert_rowid() folded into the
/// same command as the INSERT (Dapper.FSharp's insert{} CE cannot resolve excludeColumn without a
/// bound for-loop variable), and List.iter rather than a CE for loop when writing N rows, since
/// HandleErrorBuilder defines no Combine.
module MyDogsbody.Database.TemplateStore

open System
open Microsoft.Data.Sqlite
open Dapper
open Dapper.FSharp.SQLite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Database.Models

let private runSync (task: System.Threading.Tasks.Task<'T>) : 'T =
    task |> Async.AwaitTask |> Async.RunSynchronously

let private insertTemplateRow
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    (record: InvoiceTemplateRecord)
    : int =
    connection.ExecuteScalarAsync<int64>(
        "INSERT INTO InvoiceTemplates (SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (@SupplierId, @Name, @DocumentPart, @AttachmentFormat, @Position);
         SELECT last_insert_rowid();",
        {|
            SupplierId = record.SupplierId
            Name = record.Name
            DocumentPart = record.DocumentPart
            AttachmentFormat = record.AttachmentFormat
            Position = record.Position
        |},
        transaction
    )
    |> runSync
    |> int

let private insertFieldRuleRow
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    (record: TemplateFieldRuleRecord)
    : unit =
    connection.ExecuteAsync(
        "INSERT INTO TemplateFieldRules
            (TemplateId, TargetField, RuleKind, RuleText, RuleOffset, RuleSourceField, HintKind, HintText)
         VALUES (@TemplateId, @TargetField, @RuleKind, @RuleText, @RuleOffset, @RuleSourceField, @HintKind, @HintText);",
        {|
            TemplateId = record.TemplateId
            TargetField = record.TargetField
            RuleKind = record.RuleKind
            RuleText = record.RuleText
            RuleOffset = record.RuleOffset
            RuleSourceField = record.RuleSourceField
            HintKind = record.HintKind
            HintText = record.HintText
        |},
        transaction
    )
    |> runSync
    |> ignore

/// A row that cannot be mapped back is a data-integrity failure, not something a user did, so it
/// is raised and caught like any other unexpected failure rather than returned as a value. Same
/// idiom as SupplierStore.mapOrRaise.
let private mapOrRaise (mapResult: Result<StoredTemplate, string>) =
    match mapResult with
    | Ok stored -> stored
    | Error reason -> raise (InvalidOperationException $"Stored template is unusable: {reason}")

/// Runs `work` with the connection open and inside a transaction, committing on success. Any
/// exception - including one raised deliberately by mapOrRaise or TemplateRecordMappers.toRowId -
/// unwinds without a Commit, so the transaction's own Dispose rolls it back: a write in the
/// middle of a multi-statement sequence (a template row plus its field-rule rows, or several
/// position updates for a reorder) never survives a later statement's failure.
let private inTransaction (connection: SqliteConnection) (work: SqliteTransaction -> 'T) : 'T =
    connection.Open()

    try
        use transaction = connection.BeginTransaction()
        let result = work transaction
        transaction.Commit()
        result
    finally
        connection.Close()

let getForSupplier
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoiceTemplates: unit -> QuerySource<InvoiceTemplateRecord>)
    (getTemplateFieldRules: unit -> QuerySource<TemplateFieldRuleRecord>)
    (supplierId: SupplierId)
    : Result<StoredTemplate list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.TemplateStore.getForSupplier

    handleError {
        try
            let connection = getConnection ()
            let supplierRowId = int (SupplierId.value supplierId)

            let templateRows =
                select {
                    for t in getInvoiceTemplates () do
                    where (t.SupplierId = supplierRowId)
                }
                |> connection.SelectAsync<InvoiceTemplateRecord>
                |> runSync
                |> Seq.toList

            let templateIds = templateRows |> List.map (fun t -> t.Id) |> Set.ofList

            // One query for every field rule, grouped in memory, rather than one query per
            // template - getForSupplier runs on every page load and after every write, the same
            // reasoning as SupplierStore.getAll's matcher loading.
            let ruleRowsByTemplateId =
                select {
                    for r in getTemplateFieldRules () do
                    selectAll
                }
                |> connection.SelectAsync<TemplateFieldRuleRecord>
                |> runSync
                |> Seq.toList
                |> List.filter (fun r -> templateIds.Contains r.TemplateId)
                |> List.groupBy (fun r -> r.TemplateId)
                |> Map.ofList

            return
                templateRows
                |> List.map (fun row ->
                    let ruleRows = ruleRowsByTemplateId |> Map.tryFind row.Id |> Option.defaultValue []
                    TemplateRecordMappers.toStoredTemplate row ruleRows |> mapOrRaise)
        with ex ->
            return! MyDogsbodyException(action, "Failed to retrieve templates for supplier.", ex)
    }

let insertOne
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoiceTemplates: unit -> QuerySource<InvoiceTemplateRecord>)
    (getTemplateFieldRules: unit -> QuerySource<TemplateFieldRuleRecord>)
    (template: ValidTemplate)
    : Result<StoredTemplate, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.TemplateStore.insertOne

    handleError {
        try
            let connection = getConnection ()
            let supplierRowId = int (SupplierId.value (ValidTemplate.supplierId template))
            let newRecord = TemplateRecordMappers.toNewTemplateRecord supplierRowId template

            let insertedId =
                inTransaction connection (fun transaction ->
                    let insertedId = insertTemplateRow connection transaction newRecord

                    // List.iter, not a CE `for` loop: HandleErrorBuilder defines no Combine, so a
                    // `for` here could not be sequenced with the `return` that follows it.
                    ValidTemplate.rules template
                    |> List.iter (fun rule ->
                        TemplateRecordMappers.toNewTemplateFieldRuleRecord insertedId rule
                        |> insertFieldRuleRow connection transaction)

                    insertedId)

            // Every field is already known from the input plus the id the insert assigned - no
            // need to read back what was just written.
            return
                TemplateRecordMappers.toStoredTemplate
                    { newRecord with Id = insertedId }
                    (ValidTemplate.rules template |> List.map (TemplateRecordMappers.toNewTemplateFieldRuleRecord insertedId))
                |> mapOrRaise
        with ex ->
            return! MyDogsbodyException(action, "Failed to insert new template.", ex)
    }

/// Ok None means no row carried that identifier. Reporting it rather than silently succeeding is
/// what lets EditTemplateWorkflow decide that absence is TemplateNotFound.
let updateOne
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoiceTemplates: unit -> QuerySource<InvoiceTemplateRecord>)
    (getTemplateFieldRules: unit -> QuerySource<TemplateFieldRuleRecord>)
    (templateId: TemplateId)
    (template: ValidTemplate)
    : Result<StoredTemplate option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.TemplateStore.updateOne

    handleError {
        try
            let connection = getConnection ()
            let rowId = TemplateRecordMappers.toRowId templateId

            let existing =
                select {
                    for t in getInvoiceTemplates () do
                    where (t.Id = rowId)
                }
                |> connection.SelectAsync<InvoiceTemplateRecord>
                |> runSync
                |> Seq.tryHead

            match existing with
            | None -> return None
            | Some _ ->
                let supplierRowId = int (SupplierId.value (ValidTemplate.supplierId template))
                let documentPart, attachmentFormat = TemplateRecordMappers.toDocumentPartColumns (ValidTemplate.part template)

                inTransaction connection (fun transaction ->
                    update {
                        for t in getInvoiceTemplates () do
                        setColumn t.SupplierId supplierRowId
                        setColumn t.Name (TemplateName.value (ValidTemplate.name template))
                        setColumn t.DocumentPart documentPart
                        setColumn t.AttachmentFormat attachmentFormat
                        setColumn t.Position (ValidTemplate.position template)
                        where (t.Id = rowId)
                    }
                    |> fun query -> connection.UpdateAsync(query, transaction)
                    |> runSync
                    |> ignore

                    // The rule set is replaced, not merged - every existing row is removed and
                    // the submitted list inserted fresh, matching EditTemplateWorkflow's own
                    // replace-not-merge behaviour at the storage level too.
                    delete {
                        for r in getTemplateFieldRules () do
                        where (r.TemplateId = rowId)
                    }
                    |> fun query -> connection.DeleteAsync(query, transaction)
                    |> runSync
                    |> ignore

                    ValidTemplate.rules template
                    |> List.iter (fun rule ->
                        TemplateRecordMappers.toNewTemplateFieldRuleRecord rowId rule
                        |> insertFieldRuleRow connection transaction))

                // Every field is already known from the input plus the row id already confirmed
                // to exist above - no need to read back what was just written.
                let updatedRecord: InvoiceTemplateRecord =
                    {
                        Id = rowId
                        SupplierId = supplierRowId
                        Name = TemplateName.value (ValidTemplate.name template)
                        DocumentPart = documentPart
                        AttachmentFormat = attachmentFormat
                        Position = ValidTemplate.position template
                    }

                return
                    TemplateRecordMappers.toStoredTemplate
                        updatedRecord
                        (ValidTemplate.rules template |> List.map (TemplateRecordMappers.toNewTemplateFieldRuleRecord rowId))
                    |> mapOrRaise
                    |> Some
        with ex ->
            return! MyDogsbodyException(action, "Failed to update existing template.", ex)
    }

/// True when a row carried that identifier and was removed; false when it did not. Field rules
/// are removed by the database's own cascade (migration ...0003), which is why this needs no
/// getTemplateFieldRules of its own.
let deleteOne
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoiceTemplates: unit -> QuerySource<InvoiceTemplateRecord>)
    (id: TemplateId)
    : Result<bool, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.TemplateStore.deleteOne

    handleError {
        try
            let connection = getConnection ()
            let rowId = TemplateRecordMappers.toRowId id

            let existing =
                select {
                    for t in getInvoiceTemplates () do
                    where (t.Id = rowId)
                }
                |> connection.SelectAsync<InvoiceTemplateRecord>
                |> runSync
                |> Seq.tryHead

            match existing with
            | None -> return false
            | Some _ ->
                delete {
                    for t in getInvoiceTemplates () do
                    where (t.Id = rowId)
                }
                |> connection.DeleteAsync
                |> runSync
                |> ignore

                return true
        with ex ->
            return! MyDogsbodyException(action, "Failed to delete existing template.", ex)
    }

/// Persists a new Position for every template named, atomically - a failure partway through
/// (including an id that fails to parse, via TemplateRecordMappers.toRowId) must leave every
/// row's Position exactly as it was, never a partial reorder. The WHERE clause's SupplierId check
/// is defence in depth: ReorderTemplatesWorkflow has already confirmed every id belongs to this
/// supplier before calling here, but the check is free to repeat.
let reorder
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getInvoiceTemplates: unit -> QuerySource<InvoiceTemplateRecord>)
    (supplierId: SupplierId)
    (templateIds: TemplateId list)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.TemplateStore.reorder

    handleError {
        try
            let connection = getConnection ()
            let supplierRowId = int (SupplierId.value supplierId)

            inTransaction connection (fun transaction ->
                templateIds
                |> List.iteri (fun index templateId ->
                    let rowId = TemplateRecordMappers.toRowId templateId

                    update {
                        for t in getInvoiceTemplates () do
                        setColumn t.Position index
                        where (t.Id = rowId && t.SupplierId = supplierRowId)
                    }
                    |> fun query -> connection.UpdateAsync(query, transaction)
                    |> runSync
                    |> ignore))

            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to persist the new template order.", ex)
    }
