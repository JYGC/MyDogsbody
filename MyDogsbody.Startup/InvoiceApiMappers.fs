/// The top mapping point for the ledger: domain type <-> MyDogsbody.UI.Types record, plus the
/// InvoiceError <-> MyDogsbodyException translation used by both the invoice factory and the
/// scan-window factory (both areas' workflows return InvoiceError).
///
/// Total functions, no module-level bindings.
module MyDogsbody.Startup.InvoiceApiMappers

open System
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.UI.Types

let private supplierName (namesById: Map<string, string>) (id: SupplierId) : string =
    namesById
    |> Map.tryFind (SupplierId.value id)
    |> Option.defaultValue $"(unknown supplier {SupplierId.value id})"

/// The sentence the problems view shows for each cause. EXHAUSTIVE over ScanProblemCause: a
/// ninth case breaks this build.
let causeSentence (namesById: Map<string, string>) (cause: ScanProblemCause) : string =
    match cause with
    | NoSupplierMatched -> "No supplier's matchers recognised this message."
    | SeveralSuppliersMatched ids ->
        let names = ids |> List.map (supplierName namesById) |> String.concat ", "
        $"More than one supplier matched this message: {names}. Narrow their matchers so only one does."
    | NoTemplateMatched supplierId ->
        $"No template is set up for {supplierName namesById supplierId}."
    | RuleFoundNothing(supplierId, _, field) ->
        $"{supplierName namesById supplierId}'s template found nothing for the {field} field."
    | AttachmentUnreadable(fileName, reason) -> $"The attachment '{fileName}' could not be read: {reason}."
    | FormatUnsupported(fileName, format) ->
        $"The attachment '{fileName}' is a .{format} file, which has no reader."
    | ValueUnparseable(field, raw) -> $"The {field} value '{raw}' could not be used."
    | RuleTimedOutCause(supplierId, _, field) ->
        $"{supplierName namesById supplierId}'s rule for the {field} field took too long and was stopped."

let toProblemUiType (namesById: Map<string, string>) (problem: ScanProblem) : ScanProblemUiType =
    { SourceMessageId = SourceMessageId.value problem.SourceMessageId
      Sender = problem.Sender
      Subject = problem.Subject
      ReceivedAt = problem.ReceivedAt
      Cause = causeSentence namesById problem.Cause
      RecordedAt = problem.RecordedAt }

[<Literal>]
let NoDueDateReason =
    "No due date was found, so this invoice cannot be added to a calendar."

let toInvoiceUiType (namesById: Map<string, string>) (stored: StoredInvoice) : InvoiceUiType =
    let hasDueDate = Option.isSome stored.Invoice.DueDate

    { Id = InvoiceId.value stored.Id
      SupplierName = supplierName namesById stored.Invoice.SupplierId
      Reference = InvoiceReference.value stored.Invoice.Reference
      Amount = Money.amount stored.Invoice.Amount
      Currency = Money.currency stored.Invoice.Amount
      IssueDate = stored.Invoice.IssueDate |> Option.map InvoiceIssueDate.value
      DueDate = stored.Invoice.DueDate |> Option.map InvoiceDueDate.value
      MessageReceivedAt = stored.Invoice.MessageReceivedAt
      CanBecomeCalendarEvent = hasDueDate
      CannotUploadReason = (if hasDueDate then None else Some NoDueDateReason) }

let toTombstoneUiType (namesById: Map<string, string>) (tombstone: InvoiceTombstone) : TombstoneUiType =
    { SupplierId = SupplierId.value tombstone.SupplierId
      SupplierName = supplierName namesById tombstone.SupplierId
      Reference = InvoiceReference.value tombstone.Reference
      DeletedAt = tombstone.DeletedAt }

/// Outbound: a domain error becomes the exception the UI renders. Nothing here logs - this is
/// returned inside Result.mapError, never inside a handleError block, and a store failure was
/// already logged once by the adapter. The ApplicationException on the expected cases marks them
/// for ExceptionHelpers.isApplicationException; InvoiceStoreFailed is left unmarked because it
/// genuinely was an exception (though it, too, is not re-logged here).
let toMyDogsbodyException (action: string) (error: InvoiceError) : MyDogsbodyException =
    let expected (message: string) =
        MyDogsbodyException(action, message, ApplicationException message)

    match error with
    | SupplierNotRecognised sender -> expected $"No supplier matched the sender '{sender}'."
    | MultipleSuppliersMatched(sender, _) -> expected $"More than one supplier matched '{sender}'."
    | NoTemplateForSupplier id -> expected $"No template is set up for supplier '{SupplierId.value id}'."
    | TemplateMatchedNothing(_, field) -> expected $"A rule for {field} found nothing."
    | AmountUnparseable(field, raw) -> expected $"The {field} value '{raw}' is not a number."
    | DateUnparseable(field, raw, format) -> expected $"The {field} value '{raw}' does not match '{format}'."
    | DueDateOutOfRange(_, issued, days) ->
        let issuedText = issued.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        expected $"Adding {days} days to {issuedText} runs past the last date a calendar can hold."
    | RuleTimedOut(_, field) -> expected $"A rule for {field} took too long and was stopped."
    | InvoiceReferenceInvalid raw -> expected $"'{raw}' is not a usable invoice reference."
    | AmountInvalid raw -> expected $"'{raw}' is not a usable amount."
    | CurrencyInvalid raw -> expected $"'{raw}' is not a usable currency."
    | SupplierGone id -> expected $"The supplier '{SupplierId.value id}' no longer exists."
    | ScanWindowInvalid reason -> expected reason
    | ScanWindowAlreadyExists days -> expected $"A {days}-day scan window already exists."
    | CannotDeleteLastScanWindow -> expected "The last scan window cannot be deleted."
    | ScanWindowNotFound days -> expected $"There is no {days}-day scan window."
    | InvoiceNotFound -> expected "That invoice is no longer in the ledger."
    | NoAccountSelected -> expected "No mail account is selected. Choose one on the Thunderbird accounts page."
    | InvoiceStoreFailed message -> MyDogsbodyException(action, message)

/// Inbound: an adapter's exception becomes the one InvoiceError case that stands for
/// infrastructure failure. The adapter's handleError has already logged it.
let toInvoiceError (ex: MyDogsbodyException) : InvoiceError = InvoiceStoreFailed ex.Message
