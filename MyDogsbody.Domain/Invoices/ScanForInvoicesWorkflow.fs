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
        // An attachment that could not be read, or whose format has no reader, is the more useful
        // diagnostic whenever the message yielded nothing: it is a fact ABOUT THE MESSAGE, whereas
        // every other cause here is a conclusion about the template, and it is recorded nowhere
        // else - ScanMessageWorkflow hands it over exactly once, in this list. Reported only for a
        // message that produced no invoice, since a scan records one problem per message and a
        // message that yielded an invoice has its rows cleared by clearScanProblems.
        //
        // requirements.md names these two among the eight distinguishable causes, asks that an
        // unsupported format be named "so the question of whether to build a reader for it can
        // later be answered from data", says a legacy .doc "SHALL NOT" be skipped silently, and
        // pins the case outright: "WHEN an attachment is empty or zero bytes THE SYSTEM SHALL
        // report it as unreadable RATHER THAN AS TEXT THAT MATCHED NOTHING."
        //
        // Consulting the list only when NO supplier matched left both unreachable for a CONFIGURED
        // supplier - the case the feature exists for - and produced two measured wrong diagnostics:
        //
        //   RuleFoundNothing(acme, acme-t1, "Reference")  - literally the sentence the requirement
        //                                                   above forbids, for an unreadable PDF;
        //   NoTemplateMatched(acme)                       - for a supplier that HAS a PDF template.
        //
        // The second is the worse of the two and is why this covers the whole selectTemplate
        // branch: SelectTemplateWorkflow filters out every template whose DocumentPart the message
        // does not carry, and an attachment that failed to read is not among the message's parts.
        // So the one supplier configured correctly is told to go and configure a template that is
        // already there. outcome.md's 12.5 run recorded NoTemplateMatched twice against the real
        // mailbox.
        let orAttachmentCause (conclusion: ScanProblemCause) : ScanProblemCause =
            match attachmentCauses with
            | cause :: _ -> cause
            | [] -> conclusion

        match MatchSupplierWorkflow.matchSupplier suppliers scanned with
        // Decided BEFORE any template is tried, so the attachment is not the diagnostic: the
        // matchers have to be narrowed whatever the attachment turned out to be.
        | Error(MultipleSuppliersMatched(_, ids)) -> return Recorded(SeveralSuppliersMatched ids)
        | Error _ ->
            // matchSupplier only ever returns SupplierNotRecognised here.
            return Recorded(orAttachmentCause NoSupplierMatched)
        | Ok supplierId ->
            let supplier = suppliers |> List.find (fun s -> s.Id = supplierId)

            let! templates =
                loadTemplatesForSupplier supplierId |> Result.mapError fromTemplateError

            match SelectTemplateWorkflow.selectTemplate supplier.PaymentTermDays supplierId templates scanned with
            | Error selectError -> return Recorded(orAttachmentCause (toProblemCause supplierId selectError))
            | Ok extracted ->
                match ValidateInvoiceWorkflow.validateInvoice scanned.ReceivedAt extracted with
                | Error validationError ->
                    return Recorded(orAttachmentCause (toProblemCause supplierId validationError))
                | Ok invoice ->
                    let key = SupplierId.value invoice.SupplierId, InvoiceReference.value invoice.Reference

                    if Set.contains key tombstonedKeys then
                        return Skipped
                    else
                        return Extracted invoice
    }

/// Everything after `readMailFolder` runs with every folder's watermark already advanced to EOF -
/// `MailFolderReader.readFolder` saves it as part of reading, before a single message is processed.
/// So ANY abort from there on strands the mail this scan read behind an "already read" mark:
/// `resumeOffset` resumes from `OffsetReached` whenever the file has only grown, so the next scan
/// answers "nothing new" for messages that never became an invoice or a problem. No invoice, no
/// problem, nothing on screen (design.md -> Decisions taken #17; requirements.md -> "SHALL NOT
/// advance them past mail it read but never turned into an invoice or a problem").
///
/// The ORIGINAL error is returned whether or not the clear succeeded, so a broken store - the usual
/// cause of an abort here - does not mask itself behind a second failure.
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
        // them - not only the ScanAcc.Fatal one. See resettingWatermarksOnError.
        let onAbortResetWatermarks outcome =
            resettingWatermarksOnError clearWatermarks accountId outcome

        let! suppliers =
            loadSuppliers () |> Result.mapError fromSupplierError |> onAbortResetWatermarks

        let! tombstones = loadTombstones () |> onAbortResetWatermarks

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
        // This scan is aborting with some or none of the messages handled, over watermarks
        // readMailFolder already advanced to EOF - so reset them on the way out.
        | Some error -> return! onAbortResetWatermarks (Error error)
        | None ->
            let problems = List.rev final.Recorded
            let succeeded = List.rev final.Succeeded

            // Persist this scan's problems, then clear the rows for messages that now succeeded.
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
