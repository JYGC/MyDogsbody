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

let private toProblemCause (matchedSupplierId: SupplierId) (error: InvoiceError) : ScanProblemCause =
    match error with
    | SupplierNotRecognised _ -> NoSupplierMatched
    | MultipleSuppliersMatched(_, ids) -> SeveralSuppliersMatched ids
    | NoTemplateForSupplier sid -> NoTemplateMatched sid
    | TemplateMatchedNothing(templateId, field) -> RuleFoundNothing(matchedSupplierId, templateId, string field)
    | RuleTimedOut(templateId, field) -> RuleTimedOutCause(matchedSupplierId, templateId, string field)
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

type private MessageOutcome =
    | InvoiceToStore of ValidInvoice
    | ProblemToRecord of ScanProblemCause
    | NothingBecauseTheKeyIsTombstoned

let private processMessage
    (suppliers: StoredSupplier list)
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (tombstonedKeys: Set<string * string>)
    (scanned: ScannedMessage)
    (attachmentCauses: ScanProblemCause list)
    : Result<MessageOutcome, InvoiceError> =
    result {
        // Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ScanForInvoicesWorkflow.fs: orAttachmentCause
        let orAttachmentCause (conclusion: ScanProblemCause) : ScanProblemCause =
            match attachmentCauses with
            | cause :: _ -> cause
            | [] -> conclusion

        match MatchSupplierWorkflow.matchSupplier suppliers scanned with
        // Decided BEFORE any template is tried, so the attachment is not the diagnostic: the
        // matchers have to be narrowed whatever the attachment turned out to be.
        | Error(MultipleSuppliersMatched(_, ids)) -> return ProblemToRecord(SeveralSuppliersMatched ids)
        | Error _ ->
            // matchSupplier only ever returns SupplierNotRecognised here.
            return ProblemToRecord(orAttachmentCause NoSupplierMatched)
        | Ok supplierId ->
            let supplier = suppliers |> List.find (fun candidateSupplier -> candidateSupplier.Id = supplierId)

            let! templates =
                loadTemplatesForSupplier supplierId |> Result.mapError fromTemplateError

            match SelectTemplateWorkflow.selectTemplate supplier.PaymentTermDays supplierId templates scanned with
            | Error selectError -> return ProblemToRecord(orAttachmentCause (toProblemCause supplierId selectError))
            | Ok extracted ->
                match ValidateInvoiceWorkflow.validateInvoice scanned.ReceivedAt extracted with
                | Error validationError ->
                    return ProblemToRecord(orAttachmentCause (toProblemCause supplierId validationError))
                | Ok invoice ->
                    let key = SupplierId.value invoice.SupplierId, InvoiceReference.value invoice.Reference

                    if Set.contains key tombstonedKeys then
                        return NothingBecauseTheKeyIsTombstoned
                    else
                        return InvoiceToStore invoice
    }

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ScanForInvoicesWorkflow.fs: resettingWatermarksOnError
let private resettingWatermarksOnError
    (clearWatermarks: ClearWatermarks)
    (accountId: MailAccountId)
    (outcome: Result<'T, InvoiceError>)
    : Result<'T, InvoiceError> =
    match outcome with
    | Ok value -> Ok value
    | Error error ->
        clearWatermarks accountId
        |> Result.mapError (fun _ -> error)
        |> Result.bind (fun () -> Error error)

/// Once the fatal error is set, no further message is processed and the scan returns that error
/// rather than a partial result.
type private ScanAccumulator =
    { Stored: StoredInvoice list
      Recorded: ScanProblem list
      Succeeded: SourceMessageId list
      FatalErrorThatShortCircuitsTheScan: InvoiceError option }

let scanForInvoices
    (getCurrentTime: GetCurrentTime)
    (loadSelectedMailAccount: LoadSelectedMailAccount)
    (clearWatermarks: ClearWatermarks)
    (readMailFolder: ReadMailFolder)
    (readDocumentText: ReadDocumentText)
    (loadSuppliers: LoadSuppliers)
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (loadTombstones: LoadTombstones)
    (upsertInvoice: UpsertInvoice)
    (saveScanProblems: SaveScanProblems)
    (clearScanProblems: ClearScanProblems)
    (mode: ScanMode)
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

        // FullRescan ("Rescan everything") discards the watermarks so every folder is read in
        // full: a folder scanned before a supplier existed advanced to EOF having extracted
        // nothing, and an IncrementalScan would resume from there and see none of that mail
        // (design.md -> Decisions taken #16).
        do!
            match mode with
            | FullRescan -> clearWatermarks accountId |> Result.mapError fromMailAccountError
            | IncrementalScan -> Ok()

        let! messages = readMailFolder accountId cutoff |> Result.mapError fromMailAccountError

        // Past this line every folder's watermark is at EOF, so every abort below has to reset
        // them - not only the ScanAccumulator.FatalErrorThatShortCircuitsTheScan one. See
        // resettingWatermarksOnError.
        let onAbortResetWatermarks outcome =
            resettingWatermarksOnError clearWatermarks accountId outcome

        let! suppliers =
            loadSuppliers () |> Result.mapError fromSupplierError |> onAbortResetWatermarks

        let! tombstones = loadTombstones () |> onAbortResetWatermarks

        let tombstonedKeys =
            tombstones
            |> List.map (fun tombstone -> SupplierId.value tombstone.SupplierId, InvoiceReference.value tombstone.Reference)
            |> Set.ofList

        let problemFor (scanned: ScannedMessage) (cause: ScanProblemCause) : ScanProblem =
            { SourceMessageId = scanned.SourceMessageId
              Sender = scanned.Sender
              Subject = scanned.Subject
              ReceivedAt = scanned.ReceivedAt
              Cause = cause
              RecordedAt = getCurrentTime () }

        let step (accumulator: ScanAccumulator) (message: MailMessage) : ScanAccumulator =
            match accumulator.FatalErrorThatShortCircuitsTheScan with
            | Some _ -> accumulator
            | None ->
                let scanned, attachmentCauses = ScanMessageWorkflow.scanMessage readDocumentText message

                match processMessage suppliers loadTemplatesForSupplier tombstonedKeys scanned attachmentCauses with
                | Error error -> { accumulator with FatalErrorThatShortCircuitsTheScan = Some error }
                | Ok NothingBecauseTheKeyIsTombstoned -> accumulator
                | Ok(ProblemToRecord cause) ->
                    { accumulator with Recorded = problemFor scanned cause :: accumulator.Recorded }
                | Ok(InvoiceToStore invoice) ->
                    match upsertInvoice invoice with
                    | Ok storedInvoice ->
                        { accumulator with
                            Stored = storedInvoice :: accumulator.Stored
                            Succeeded = scanned.SourceMessageId :: accumulator.Succeeded }
                    | Error(SupplierGone _) ->
                        { accumulator with Recorded = problemFor scanned NoSupplierMatched :: accumulator.Recorded }
                    | Error error -> { accumulator with FatalErrorThatShortCircuitsTheScan = Some error }

        let final =
            messages
            |> List.fold step { Stored = []; Recorded = []; Succeeded = []; FatalErrorThatShortCircuitsTheScan = None }

        match final.FatalErrorThatShortCircuitsTheScan with
        // This scan is aborting with some or none of the messages handled, over watermarks
        // readMailFolder already advanced to EOF - so reset them on the way out.
        | Some error -> return! onAbortResetWatermarks (Error error)
        | None ->
            let problems = List.rev final.Recorded
            let succeeded = List.rev final.Succeeded

            // clearScanProblems only touches the ids passed - a narrower window does not erase
            // diagnostics for messages outside it (design decision 4).
            //
            // Both reset the watermarks if they fail: every message HAS been processed by now, but
            // the diagnostics that processing produced are exactly what such a failure loses, and
            // they are re-derivable only by reading the mail again. A saveScanProblems failure over
            // an advanced watermark leaves the problem list empty for that mail for good.
            do! (if List.isEmpty problems then Ok() else saveScanProblems problems) |> onAbortResetWatermarks
            do! (if List.isEmpty succeeded then Ok() else clearScanProblems succeeded) |> onAbortResetWatermarks

            return
                { Invoices = List.rev final.Stored
                  Problems = problems }
    }
