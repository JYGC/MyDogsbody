/// The bottom mapping point for the ledger: the plain SQLite record (int identity, TEXT dates
/// and amount) <-> domain type. Pure - no I/O, no handleError. InvoiceStore does the talking;
/// this file only translates, so it is asserted field-for-field without a database.
module MyDogsbody.Database.InvoiceRecordMappers

open System
open System.Globalization
open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database.Models

let private invariant = CultureInfo.InvariantCulture

/// ISO 8601 round-trip ("o") for timestamps, yyyy-MM-dd for the date-only issue/due dates.
let private timestamp (value: DateTime) = value.ToString("o", invariant)
let private dateOnly (value: DateTime) = value.ToString("yyyy-MM-dd", invariant)

let private parseTimestamp (field: string) (value: string) : Result<DateTime, string> =
    match DateTime.TryParse(value, invariant, DateTimeStyles.RoundtripKind) with
    | true, parsed -> Ok parsed
    | false, _ -> Error $"Stored {field} '{value}' is not a valid timestamp."

let private parseDateOnly (field: string) (value: string) : Result<DateTime, string> =
    match DateTime.TryParseExact(value, "yyyy-MM-dd", invariant, DateTimeStyles.None) with
    | true, parsed -> Ok parsed
    | false, _ -> Error $"Stored {field} '{value}' is not a valid date."

/// Only ever called on an id that came from a row already read - a non-numeric value is a
/// data-integrity failure the store raises, the same way SupplierRecordMappers.toRowId does.
let toRowId (id: InvoiceId) : int = int (InvoiceId.value id)
let supplierRowId (id: SupplierId) : int = int (SupplierId.value id)
let templateRowId (id: TemplateId) : int = int (TemplateId.value id)

// ---------------- Invoice ----------------

let toNewInvoiceRecord (invoice: ValidInvoice) : InvoiceRecord =
    { Id = 0
      SupplierId = supplierRowId invoice.SupplierId
      TemplateId = templateRowId invoice.TemplateId
      Reference = InvoiceReference.value invoice.Reference
      Amount = (Money.amount invoice.Amount).ToString(invariant)
      Currency = Money.currency invoice.Amount
      IssueDate = invoice.IssueDate |> Option.map (InvoiceIssueDate.value >> dateOnly)
      DueDate = invoice.DueDate |> Option.map (InvoiceDueDate.value >> dateOnly)
      SourceMessageId = SourceMessageId.value invoice.SourceMessageId
      MessageReceivedAt = timestamp invoice.MessageReceivedAt
      ScannedAt = "" } // set by the store at write time

let toStoredInvoice (row: InvoiceRecord) : Result<StoredInvoice, string> =
    result {
        let! id = InvoiceId.create (string row.Id)
        let! supplierId = SupplierId.create (string row.SupplierId)
        let! templateId = TemplateId.create (string row.TemplateId)
        let! reference = InvoiceReference.create row.Reference

        let! amount =
            match Decimal.TryParse(row.Amount, NumberStyles.Number, invariant) with
            | true, parsed -> Ok parsed
            | false, _ -> Error $"Stored amount '{row.Amount}' is not a number."

        let! money = Money.create amount row.Currency
        let! sourceMessageId = SourceMessageId.create row.SourceMessageId
        let! messageReceivedAt = parseTimestamp "MessageReceivedAt" row.MessageReceivedAt
        let! scannedAt = parseTimestamp "ScannedAt" row.ScannedAt

        let! issueDate =
            match row.IssueDate with
            | None -> Ok None
            | Some value -> parseDateOnly "IssueDate" value |> Result.bind InvoiceIssueDate.create |> Result.map Some

        let! dueDate =
            match row.DueDate with
            | None -> Ok None
            | Some value -> parseDateOnly "DueDate" value |> Result.bind InvoiceDueDate.create |> Result.map Some

        return
            { Id = id
              ScannedAt = scannedAt
              Invoice =
                { SupplierId = supplierId
                  TemplateId = templateId
                  SourceMessageId = sourceMessageId
                  Reference = reference
                  Amount = money
                  IssueDate = issueDate
                  DueDate = dueDate
                  MessageReceivedAt = messageReceivedAt } }
    }

// ---------------- ScanProblem: cause <-> (Cause, Detail, SupplierId) ----------------

/// The field separator inside Detail. ASCII Unit Separator - it does not occur in a filename, a
/// reference, a reason string or a stringified field name, so a split on it is unambiguous.
[<Literal>]
let private US = '\u001f'

/// Domain -> persistence. EXHAUSTIVE over ScanProblemCause: a ninth case breaks this build.
let encodeCause (cause: ScanProblemCause) : string * string option * int option =
    match cause with
    | NoSupplierMatched -> "NoSupplierMatched", None, None
    | SeveralSuppliersMatched ids ->
        "SeveralSuppliersMatched",
        Some(ids |> List.map SupplierId.value |> String.concat (string US)),
        None
    | NoTemplateMatched supplierId -> "NoTemplateMatched", None, Some(supplierRowId supplierId)
    | RuleFoundNothing(supplierId, templateId, field) ->
        "RuleFoundNothing", Some $"{TemplateId.value templateId}{US}{field}", Some(supplierRowId supplierId)
    | AttachmentUnreadable(fileName, reason) -> "AttachmentUnreadable", Some $"{fileName}{US}{reason}", None
    | FormatUnsupported(fileName, format) -> "FormatUnsupported", Some $"{fileName}{US}{format}", None
    | ValueUnparseable(field, raw) -> "ValueUnparseable", Some $"{field}{US}{raw}", None
    | RuleTimedOutCause(supplierId, templateId, field) ->
        "RuleTimedOutCause", Some $"{TemplateId.value templateId}{US}{field}", Some(supplierRowId supplierId)

let private parts (detail: string option) : string list =
    match detail with
    | None -> []
    | Some value -> value.Split(US) |> Array.toList

/// Persistence -> domain. Returns Result: a row from an older build, or edited by hand, can carry
/// a Cause string or a Detail shape no current build declares.
let decodeCause (causeName: string) (detail: string option) (supplierRowId: int option) : Result<ScanProblemCause, string> =
    let supplierId () =
        match supplierRowId with
        | Some rowId -> SupplierId.create (string rowId)
        | None -> Error $"Stored cause '{causeName}' needs a SupplierId column and none was set."

    let templateId (value: string) = TemplateId.create value

    match causeName, parts detail with
    | "NoSupplierMatched", _ -> Ok NoSupplierMatched
    | "SeveralSuppliersMatched", ids when not (List.isEmpty ids) ->
        ids
        |> List.map SupplierId.create
        |> List.fold
            (fun acc next ->
                match acc, next with
                | Ok list, Ok id -> Ok(id :: list)
                | Error e, _ -> Error e
                | _, Error e -> Error e)
            (Ok [])
        |> Result.map (List.rev >> SeveralSuppliersMatched)
    | "NoTemplateMatched", _ -> supplierId () |> Result.map NoTemplateMatched
    | "RuleFoundNothing", [ tid; field ] ->
        result {
            let! s = supplierId ()
            let! t = templateId tid
            return RuleFoundNothing(s, t, field)
        }
    | "AttachmentUnreadable", [ fileName; reason ] -> Ok(AttachmentUnreadable(fileName, reason))
    | "FormatUnsupported", [ fileName; format ] -> Ok(FormatUnsupported(fileName, format))
    | "ValueUnparseable", [ field; raw ] -> Ok(ValueUnparseable(field, raw))
    | "RuleTimedOutCause", [ tid; field ] ->
        result {
            let! s = supplierId ()
            let! t = templateId tid
            return RuleTimedOutCause(s, t, field)
        }
    | unknown, _ -> Error $"Stored scan-problem cause '{unknown}' with detail {detail} has no domain equivalent."

let toNewScanProblemRecord (problem: ScanProblem) : ScanProblemRecord =
    let causeName, detail, supplierRowId = encodeCause problem.Cause

    { Id = 0
      SourceMessageId = SourceMessageId.value problem.SourceMessageId
      SupplierId = supplierRowId
      Sender = problem.Sender
      Subject = problem.Subject
      ReceivedAt = timestamp problem.ReceivedAt
      Cause = causeName
      Detail = detail
      RecordedAt = timestamp problem.RecordedAt }

let toScanProblem (row: ScanProblemRecord) : Result<ScanProblem, string> =
    result {
        let! sourceMessageId = SourceMessageId.create row.SourceMessageId
        let! receivedAt = parseTimestamp "ReceivedAt" row.ReceivedAt
        let! recordedAt = parseTimestamp "RecordedAt" row.RecordedAt
        let! cause = decodeCause row.Cause row.Detail row.SupplierId

        return
            { SourceMessageId = sourceMessageId
              Sender = row.Sender
              Subject = row.Subject
              ReceivedAt = receivedAt
              Cause = cause
              RecordedAt = recordedAt }
    }

// ---------------- Tombstone ----------------

let toNewTombstoneRecord (tombstone: InvoiceTombstone) : InvoiceTombstoneRecord =
    { Id = 0
      SupplierId = supplierRowId tombstone.SupplierId
      Reference = InvoiceReference.value tombstone.Reference
      DeletedAt = timestamp tombstone.DeletedAt }

// ---------------- Scan window ----------------

let toStoredScanWindow (row: ScanWindowRecord) : Result<StoredScanWindow, string> =
    result {
        let! id = ScanWindowId.create (string row.Id)
        let! days = ScanWindowDays.create row.Days
        return { Id = id; Days = days }
    }

let toInvoiceTombstone (row: InvoiceTombstoneRecord) : Result<InvoiceTombstone, string> =
    result {
        let! supplierId = SupplierId.create (string row.SupplierId)
        let! reference = InvoiceReference.create row.Reference
        let! deletedAt = parseTimestamp "DeletedAt" row.DeletedAt

        return
            { SupplierId = supplierId
              Reference = reference
              DeletedAt = deletedAt }
    }
