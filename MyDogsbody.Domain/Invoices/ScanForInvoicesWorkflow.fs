/// The scan orchestration: read the selected account, match a supplier, apply its templates,
/// validate, store; record a problem for every message that yields nothing, and continue.
///
/// Every dependency is a function value - no mail store, no database, no files - so the whole
/// thing is unit-tested with lambdas. Most of the body is calls to the pure workflows from
/// change #2.
module MyDogsbody.Domain.Invoices.ScanForInvoicesWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices

/// "The last N days" names a set of DATES, not 24*N hours (Q1.18): the cutoff is the start of the
/// day N days before today, so the same window scanned at 09:00 and at 17:00 covers the same
/// mail. Days are uniform, so there is no month-end trap.
///
/// Pure - the clock is the GetCurrentTime dependency, supplied as a fixed instant in tests.
let computeCutoff (getCurrentTime: GetCurrentTime) (window: ScanWindowDays) : ScanCutoff =
    let today = (getCurrentTime ()).Date
    ScanCutoff.ofStartOfDay (today.AddDays(-float (ScanWindowDays.value window)))

// --- sibling-area errors mapped onto InvoiceError at the point the dependency is called ---

let private fromMailAccountError (error: MailAccountError) : InvoiceError =
    match error with
    | MailAccountError.NoAccountSelected -> NoAccountSelected
    | other -> InvoiceStoreFailed $"{other}"

let private fromSupplierError (error: SupplierError) : InvoiceError = InvoiceStoreFailed $"{error}"
let private fromTemplateError (error: TemplateError) : InvoiceError = InvoiceStoreFailed $"{error}"

/// An apply-time or validation InvoiceError becomes the persisted ScanProblemCause for the
/// message it happened on. supplierId is the matched supplier - known by the time any of these
/// can arise.
let private toProblemCause (supplierId: SupplierId) (error: InvoiceError) : ScanProblemCause =
    match error with
    | SupplierNotRecognised _ -> NoSupplierMatched
    | MultipleSuppliersMatched(_, ids) -> SeveralSuppliersMatched ids
    | NoTemplateForSupplier sid -> NoTemplateMatched sid
    | TemplateMatchedNothing(templateId, field) -> RuleFoundNothing(supplierId, templateId, string field)
    | RuleTimedOut(templateId, field) -> RuleTimedOutCause(supplierId, templateId, string field)
    | AmountUnparseable(field, raw) -> ValueUnparseable(string field, raw)
    | DateUnparseable(field, raw, _) -> ValueUnparseable(string field, raw)
    | DueDateOutOfRange(_, issueDate, days) ->
        let issued = issueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        ValueUnparseable("DueDate", $"{issued} + {days} days")
    | InvoiceReferenceInvalid raw -> ValueUnparseable("Reference", raw)
    | AmountInvalid raw -> ValueUnparseable("Amount", raw)
    | CurrencyInvalid raw -> ValueUnparseable("Currency", raw)
    // Unreachable in a single scan - matchSupplier only matches a loaded supplier, and the upsert
    // is in the same scan - but guarded rather than crashing: the supplier is simply not there.
    | SupplierGone _ -> NoSupplierMatched
    // The remaining cases are scan-level, not per-message, and never reach here.
    | ScanWindowInvalid _
    | ScanWindowAlreadyExists _
    | CannotDeleteLastScanWindow
    | ScanWindowNotFound _
    | InvoiceNotFound
    | NoAccountSelected
    | InvoiceStoreFailed _ -> ValueUnparseable("(scan)", string error)

/// What one message produced: an invoice to store, a problem to record, or - for a tombstoned
/// key - nothing at all.
type private MessageOutcome =
    | Extracted of ValidInvoice
    | Recorded of ScanProblemCause
    | Skipped

let private processMessage
    (suppliers: StoredSupplier list)
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (tombstonedKeys: Set<string * string>)
    (scanned: ScannedMessage)
    (attachmentCauses: ScanProblemCause list)
    : Result<MessageOutcome, InvoiceError> =
    result {
        match MatchSupplierWorkflow.matchSupplier suppliers scanned with
        | Error(MultipleSuppliersMatched(_, ids)) -> return Recorded(SeveralSuppliersMatched ids)
        | Error _ ->
            // matchSupplier only ever returns SupplierNotRecognised here. If the message also had
            // an unreadable or unsupported attachment, that is the more useful diagnostic.
            return
                match attachmentCauses with
                | cause :: _ -> Recorded cause
                | [] -> Recorded NoSupplierMatched
        | Ok supplierId ->
            let supplier = suppliers |> List.find (fun s -> s.Id = supplierId)

            let! templates =
                loadTemplatesForSupplier supplierId |> Result.mapError fromTemplateError

            match SelectTemplateWorkflow.selectTemplate supplier.PaymentTermDays supplierId templates scanned with
            | Error selectError -> return Recorded(toProblemCause supplierId selectError)
            | Ok extracted ->
                match ValidateInvoiceWorkflow.validateInvoice scanned.ReceivedAt extracted with
                | Error validationError -> return Recorded(toProblemCause supplierId validationError)
                | Ok invoice ->
                    let key = SupplierId.value invoice.SupplierId, InvoiceReference.value invoice.Reference

                    if Set.contains key tombstonedKeys then
                        return Skipped
                    else
                        return Extracted invoice
    }

/// The running total across messages. `Fatal` short-circuits: once set, no further message is
/// processed and the scan returns that error rather than a partial result.
type private ScanAcc =
    { Stored: StoredInvoice list
      Recorded: ScanProblem list
      Succeeded: SourceMessageId list
      Fatal: InvoiceError option }

let scanForInvoices
    (getCurrentTime: GetCurrentTime)
    (loadSelectedMailAccount: LoadSelectedMailAccount)
    (readMailFolder: ReadMailFolder)
    (readDocumentText: ReadDocumentText)
    (loadSuppliers: LoadSuppliers)
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (loadTombstones: LoadTombstones)
    (upsertInvoice: UpsertInvoice)
    (saveScanProblems: SaveScanProblems)
    (clearScanProblems: ClearScanProblems)
    (window: ScanWindowDays)
    : Result<ScanResult, InvoiceError> =
    result {
        let cutoff = computeCutoff getCurrentTime window

        let! selectedAccount =
            loadSelectedMailAccount () |> Result.mapError fromMailAccountError

        let! accountId =
            match selectedAccount with
            | Some accountId -> Ok accountId
            | None -> Error NoAccountSelected

        let! messages = readMailFolder accountId cutoff |> Result.mapError fromMailAccountError
        let! suppliers = loadSuppliers () |> Result.mapError fromSupplierError
        let! tombstones = loadTombstones ()

        let tombstonedKeys =
            tombstones
            |> List.map (fun t -> SupplierId.value t.SupplierId, InvoiceReference.value t.Reference)
            |> Set.ofList

        let problemFor (scanned: ScannedMessage) (cause: ScanProblemCause) : ScanProblem =
            { SourceMessageId = scanned.SourceMessageId
              Sender = scanned.Sender
              Subject = scanned.Subject
              ReceivedAt = scanned.ReceivedAt
              Cause = cause
              RecordedAt = getCurrentTime () }

        let step (acc: ScanAcc) (message: MailMessage) : ScanAcc =
            match acc.Fatal with
            | Some _ -> acc
            | None ->
                let scanned, attachmentCauses = ScanMessageWorkflow.scanMessage readDocumentText message

                match processMessage suppliers loadTemplatesForSupplier tombstonedKeys scanned attachmentCauses with
                | Error error -> { acc with Fatal = Some error }
                | Ok Skipped -> acc
                | Ok(Recorded cause) ->
                    { acc with Recorded = problemFor scanned cause :: acc.Recorded }
                | Ok(Extracted invoice) ->
                    match upsertInvoice invoice with
                    | Ok storedInvoice ->
                        { acc with
                            Stored = storedInvoice :: acc.Stored
                            Succeeded = scanned.SourceMessageId :: acc.Succeeded }
                    | Error(SupplierGone _) ->
                        { acc with Recorded = problemFor scanned NoSupplierMatched :: acc.Recorded }
                    | Error error -> { acc with Fatal = Some error }

        let final =
            messages
            |> List.fold step { Stored = []; Recorded = []; Succeeded = []; Fatal = None }

        match final.Fatal with
        | Some error -> return! Error error
        | None ->
            let problems = List.rev final.Recorded
            let succeeded = List.rev final.Succeeded

            // Persist this scan's problems, then clear the rows for messages that now succeeded.
            // clearScanProblems only touches the ids passed - a narrower window does not erase
            // diagnostics for messages outside it (design decision 4).
            do! (if List.isEmpty problems then Ok() else saveScanProblems problems)
            do! (if List.isEmpty succeeded then Ok() else clearScanProblems succeeded)

            return
                { Invoices = List.rev final.Stored
                  Problems = problems }
    }
