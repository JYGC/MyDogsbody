namespace MyDogsbody.Domain.Invoices

open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.MailAccounts

/// The identifier of the message a scan read. Opaque to the domain, the same way SupplierId is.
/// This file is created in change #2 and extended in change #4 - do not duplicate it.
type SourceMessageId = private SourceMessageId of string

module SourceMessageId =

    let create (value: string) : Result<SourceMessageId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Source message id must not be empty."
        else
            Ok (SourceMessageId value)

    let value (SourceMessageId id) = id

    /// Never fails: a blank id is replaced by the fallback text (itself never blank). A message
    /// with no Message-ID already gets a synthesized one in the mail reader, so this only guards
    /// against a torn read - and a problem keyed by nothing is worse than one keyed by a
    /// stand-in.
    let createOrDefault (fallback: string) (value: string) : SourceMessageId =
        match create value with
        | Ok id -> id
        | Error _ ->
            if System.String.IsNullOrWhiteSpace fallback then
                SourceMessageId "unidentified-message"
            else
                SourceMessageId fallback

/// Which part of a message some text came from.
type MessagePart =
    | SubjectPart
    | BodyPart
    | AttachmentPart of name: string * format: DocumentFormat

/// A message and its attachments flattened to text, ready for a template.
///
/// Deliberately carries no SupplierId - MatchSupplierWorkflow produces one FROM this, so
/// carrying one here would be circular. The supplier lands on ExtractedInvoice instead.
type ScannedMessage =
    { SourceMessageId: SourceMessageId
      Sender: string
      Subject: string
      ReceivedAt: System.DateTime
      Parts: (MessagePart * TextLine list) list }

/// One part of a message, its text normalized once, with the provenance a line-oriented rule
/// needs: TextNormalization.NormalizedLine carries both the joined line every text-reading rule
/// sees and the laid-out lines LinesAfterLabel counts. Keeping them together is what stops the two
/// views drifting apart - they are produced by one pass, not by two functions that must agree.
type NormalizedPart =
    { Part: MessagePart
      Lines: TextNormalization.NormalizedLine list }

/// A ScannedMessage whose text has been through TextNormalization exactly once - every part's
/// lines, the subject, and every attachment filename.
///
/// A distinct stage type rather than a flag or a convention, for the two reasons CLAUDE.md gives
/// for stage types at all. It makes the normalization impossible to skip: applyTemplate takes one
/// of these and there is no way to hand it raw text. And it makes the normalization impossible to
/// repeat: it happens once per message in MessageNormalization, above the loop that tries a
/// supplier's templates in turn, rather than once per candidate template inside it - NFKC over
/// every line of every attachment is the most expensive thing in this pipeline, and it used to
/// run once for each template tried.
///
/// Carries no Sender: MatchSupplierWorkflow answers "whose message is this?" from the raw
/// ScannedMessage before a template is ever chosen, so nothing downstream of normalization needs
/// one.
type NormalizedMessage =
    private
        { SourceMessageId': SourceMessageId
          Subject': string
          Parts': NormalizedPart list }

module NormalizedMessage =

    // Read-only accessors; no constructor is exposed. MessageNormalization.normalizeMessage is
    // the only function that builds the private record literal, the same arrangement
    // ValidTemplate and ValidateTemplateWorkflow have.
    let sourceMessageId (m: NormalizedMessage) = m.SourceMessageId'
    let subject (m: NormalizedMessage) = m.Subject'
    let parts (m: NormalizedMessage) = m.Parts'

/// What a template pulled out and parsed with its own hints.
///
/// Deliberately not named UnvalidatedInvoice - it is still untrusted in the domain sense
/// (Reference is a plain string, Amount an unconstrained decimal, Currency compared to
/// nothing), but ApplyTemplateWorkflow both extracts AND parses using the hint that selected
/// it, so what comes out is named for what it is. Change #4's ValidInvoice is where
/// constrained types appear.
type ExtractedInvoice =
    { SupplierId: SupplierId
      TemplateId: TemplateId
      SourceMessageId: SourceMessageId
      Reference: string
      Amount: decimal
      Currency: string
      IssueDate: System.DateTime option
      DueDate: System.DateTime option }

/// What can go wrong turning a message into an invoice. Change #4 adds the storage and
/// mail-store cases to this same union.
///
/// DueDateOutOfRange is the one case here that is not about text: DateTime.AddDays RAISES
/// ArgumentOutOfRangeException once the result leaves DateTime's range, and an issue date read as
/// 31 Dec 9999 with any positive payment term does exactly that. The DateFromField branch that
/// derives a due date promised a Result and had no failure channel of its own, so the exception
/// would have unwound out of the domain, past a composition root that maps values rather than
/// catching, into a UI with no alert for it. Reaching it needs an absurd date on the document AND
/// a format that accepts it - unlikely, in the same way the null cases this file already guards
/// are unlikely. Carries both ends of the arithmetic, since neither alone says what happened.
type InvoiceError =
    | SupplierNotRecognised of sender: string
    | MultipleSuppliersMatched of sender: string * suppliers: SupplierId list
    | NoTemplateForSupplier of SupplierId
    | TemplateMatchedNothing of template: TemplateId * field: TargetField
    | AmountUnparseable of field: TargetField * raw: string
    | DateUnparseable of field: TargetField * raw: string * format: string
    | DueDateOutOfRange of template: TemplateId * issueDate: System.DateTime * paymentTermDays: int
    | RuleTimedOut of template: TemplateId * field: TargetField
    // --- change #4 ---
    /// A field ExtractedInvoice carried could not become its constrained type. Each carries the
    /// raw value the message is written from (task 3.3).
    | InvoiceReferenceInvalid of raw: string
    | AmountInvalid of raw: string
    | CurrencyInvalid of raw: string
    /// A scan produced an invoice for a supplier that has since been deleted - reported as a
    /// problem rather than stored as a row with no supplier (edge case).
    | SupplierGone of SupplierId
    /// A scan-window day count outside ScanWindowDays' bounds. Expected, rendered in the alert.
    | ScanWindowInvalid of reason: string
    /// Adding a window whose day count already exists. Carries the days for the message.
    | ScanWindowAlreadyExists of days: int
    /// Deleting the only remaining window. A domain rule, not a UI guard: the picker must always
    /// have something to offer.
    | CannotDeleteLastScanWindow
    /// A window was selected that is not one of the stored rows.
    | ScanWindowNotFound of days: int
    /// A delete or undelete named an invoice the ledger does not hold.
    | InvoiceNotFound
    /// The scan ran with no mail account selected (change #3). Expected, not logged.
    | NoAccountSelected
    /// The store, the mail reader, or a sibling area failed outright. Wraps the real message and
    /// is the one case here that is logged once.
    | InvoiceStoreFailed of message: string

// --- constrained primitives (task 2.1) ---

/// The supplier's own invoice number.
///
/// create FOLDS INTERNAL WHITESPACE via InvoiceText.foldReferenceWhitespace (change #2's fold,
/// not a second implementation): one measured utility prints its reference in three
/// space-separated groups and names the attachment with the same digits unspaced. Under the
/// Q5.8 natural key those would be two keys for one invoice - two ledger rows, two calendar
/// events.
type InvoiceReference = private InvoiceReference of string

module InvoiceReference =

    let create (value: string) : Result<InvoiceReference, string> =
        match InvoiceText.foldReferenceWhitespace value with
        | "" -> Error "Invoice reference must not be empty."
        | folded -> Ok(InvoiceReference folded)

    let value (InvoiceReference reference) = reference

/// An amount and its currency code. Q1.2 makes currency part of what an invoice is; Q7.6.8 makes
/// it a per-template FixedValue, overridable by a rule. 96% of measured documents carry `$` and
/// every one sampled is AUD.
type Money = private Money of decimal * string // amount, currency code

module Money =

    /// A typo guard, not a policy - the same shape as ScanWindowDays' bound. No lower bound: an
    /// amount parsed as zero or negative is stored as found (requirements.md edge case).
    /// (Not [<Literal>] - F# literal bindings cannot be decimal.)
    let MaxAbsAmount = 1_000_000_000_000m

    let create (amount: decimal) (currency: string) : Result<Money, string> =
        let trimmed = if isNull currency then "" else currency.Trim()

        if trimmed = "" then
            Error "Currency must not be empty."
        elif abs amount > MaxAbsAmount then
            Error $"Amount {amount} is implausibly large."
        else
            Ok(Money(amount, trimmed.ToUpperInvariant()))

    let amount (Money(a, _)) = a
    let currency (Money(_, c)) = c

/// The date a document states it was issued. A year guard keeps a date read as 31 Dec 9999 -
/// which DateFromField arithmetic would overflow on - out of the ledger.
///
/// Named InvoiceIssueDate, not IssueDate: TargetField (the template DSL) already has union cases
/// IssueDate and DueDate, and a type of the same name shadows them wherever both namespaces are
/// open (the composition root's TemplateApiMappers). The record fields below stay IssueDate /
/// DueDate - a field and a union case coexist, as ExtractedInvoice already shows.
type InvoiceIssueDate = private InvoiceIssueDate of System.DateTime

module InvoiceIssueDate =

    [<Literal>]
    let EarliestYear = 1900

    [<Literal>]
    let LatestYear = 3000

    let create (value: System.DateTime) : Result<InvoiceIssueDate, string> =
        if value.Year < EarliestYear || value.Year > LatestYear then
            Error $"Issue date year must be between {EarliestYear} and {LatestYear}."
        else
            Ok(InvoiceIssueDate value.Date)

    let value (InvoiceIssueDate date) = date

/// The date payment is due - the field the calendar event lands on (Q2.1), and the binding
/// constraint the measurement found: only ~1 invoice in 8 states one. Named InvoiceDueDate for
/// the same reason as InvoiceIssueDate.
type InvoiceDueDate = private InvoiceDueDate of System.DateTime

module InvoiceDueDate =

    let create (value: System.DateTime) : Result<InvoiceDueDate, string> =
        if value.Year < InvoiceIssueDate.EarliestYear || value.Year > InvoiceIssueDate.LatestYear then
            Error
                $"Due date year must be between {InvoiceIssueDate.EarliestYear} and {InvoiceIssueDate.LatestYear}."
        else
            Ok(InvoiceDueDate value.Date)

    let value (InvoiceDueDate date) = date

/// The identifier the store assigned. Opaque to the domain, the same way SupplierId is.
type InvoiceId = private InvoiceId of string

module InvoiceId =

    let create (value: string) : Result<InvoiceId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Invoice id must not be empty."
        else
            Ok(InvoiceId value)

    let value (InvoiceId id) = id

// --- scan window (task 2.2) ---

/// A window is a ROW, not a case. The seeded five are a starting set, not the whole set, so the
/// guarantee a closed union would have given moves into this create - the same move a
/// user-authored template already forces.
type ScanWindowDays = private ScanWindowDays of int

module ScanWindowDays =

    [<Literal>]
    let Minimum = 1

    [<Literal>]
    let Maximum = 3650 // a typo guard, not a policy (Q1.16)

    let create (days: int) : Result<ScanWindowDays, string> =
        if days < Minimum then
            Error "A scan window must be at least one day."
        elif days > Maximum then
            Error $"A scan window must be {Maximum} days or fewer."
        else
            Ok(ScanWindowDays days)

    let value (ScanWindowDays days) = days

    /// Seeded by migration, never hard-coded in a component.
    let seeded = [ 7; 14; 30; 90; 180 ]

    /// Used when nothing has been chosen yet, or the remembered choice no longer exists.
    let fallback = 14

    /// The same value as a ScanWindowDays. Built with the private constructor because `fallback`
    /// is inside the bounds by construction - ResolveScanWindowWorkflow needs it without a
    /// Result to unwrap.
    let fallbackWindow = ScanWindowDays fallback

type ScanWindowId = private ScanWindowId of string

module ScanWindowId =

    let create (value: string) : Result<ScanWindowId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Scan window id must not be empty."
        else
            Ok(ScanWindowId value)

    let value (ScanWindowId id) = id

type StoredScanWindow = { Id: ScanWindowId; Days: ScanWindowDays }

// --- stage types and persisted shapes (task 2.3) ---

/// An ExtractedInvoice whose every field has become its constrained type. Produced only by
/// ValidateInvoiceWorkflow. DueDate is an option - Q1.10: an invoice with no due date is stored
/// and listed, greyed out, and is simply not uploadable.
type ValidInvoice =
    { SupplierId: SupplierId
      TemplateId: TemplateId
      SourceMessageId: SourceMessageId
      Reference: InvoiceReference
      Amount: Money
      IssueDate: InvoiceIssueDate option
      DueDate: InvoiceDueDate option
      /// The date the mail that carried this invoice ARRIVED (Q1.6). Carried so the scan window -
      /// which is measured on mail-received date, not due date - can hide invoices outside it
      /// without deleting them (LoadInvoices filters on this). Not a constrained type: it comes
      /// from the mail store and is a fact, not a user value.
      MessageReceivedAt: System.DateTime }

/// A ValidInvoice that has been through the store: it has an id and a scan timestamp.
type StoredInvoice =
    { Id: InvoiceId
      Invoice: ValidInvoice
      ScannedAt: System.DateTime }

/// Why a message yielded no invoice. Persisted (Q1.19) so incremental scanning does not empty
/// the diagnostic list before it is looked at. The eight causes requirements.md enumerates.
type ScanProblemCause =
    | NoSupplierMatched
    | SeveralSuppliersMatched of SupplierId list
    | NoTemplateMatched of SupplierId
    | RuleFoundNothing of supplier: SupplierId * template: TemplateId * field: string
    | AttachmentUnreadable of fileName: string * reason: string
    | FormatUnsupported of fileName: string * format: string
    | ValueUnparseable of field: string * raw: string
    | RuleTimedOutCause of supplier: SupplierId * template: TemplateId * field: string

/// A problem row, keyed by source message id, carrying enough to act on it - a message id alone
/// is not actionable, so the sender, subject and received date travel with it.
type ScanProblem =
    { SourceMessageId: SourceMessageId
      Sender: string
      Subject: string
      ReceivedAt: System.DateTime
      Cause: ScanProblemCause
      RecordedAt: System.DateTime }

/// The Q5.14 record that keeps a hand-deleted invoice deleted. Keyed on the NATURAL key, not the
/// database id, so rebuilding the ledger does not resurrect what you removed.
type InvoiceTombstone =
    { SupplierId: SupplierId
      Reference: InvoiceReference
      DeletedAt: System.DateTime }

/// What a scan returns: the invoices it found and the problems it recorded, as two lists
/// (requirements.md - "return both ... as two lists").
type ScanResult =
    { Invoices: StoredInvoice list
      Problems: ScanProblem list }

/// Whether a scan resumes each folder from its watermark or reads every folder in full.
///
/// A choice type rather than a `bool` (CLAUDE.md coding style). A folder's watermark records how
/// far it was read, and `MailFolderReader.resumeOffset` skips a message older than the cutoff
/// BEFORE parsing its body - so a folder scanned once with no supplier configured advances to the
/// end having extracted nothing, and every `IncrementalScan` after that sees none of that mail.
/// `FullRescan` is the escape hatch: it clears the selected account's watermarks before reading,
/// so a supplier or template configured after the first scan can still be reached. The invoices
/// page's "Rescan everything" button; `Scan` and the initial load stay `IncrementalScan`.
type ScanMode =
    | IncrementalScan
    | FullRescan

// --- dependency function types (task 2.3) ---
//
// Not interfaces, not classes, not a collection getter. A workflow receives a function value, so
// a test supplies a lambda and the composition root supplies the real adapter. Errors are this
// area's InvoiceError; the scan maps sibling-area errors (SupplierError, TemplateError,
// MailAccountError, DocumentError) onto it at the point it calls those dependencies.

/// CLAUDE.md forbids the domain reading a clock, and "N days back from the start of today" needs
/// one. A published interface (friction #15) - its contract-suite rationale is in the test file.
type GetCurrentTime = unit -> System.DateTime

type LoadInvoices = ScanCutoff option -> Result<StoredInvoice list, InvoiceError>

/// Upsert on the NATURAL key (supplier + reference), one invoice at a time: a rescan of an
/// overlapping window updates rather than duplicates, and a single row that violates a
/// constraint (a supplier deleted mid-scan) becomes one message's problem rather than failing
/// the whole scan. (Design's UpsertInvoices was plural; per-row is what "continue past a failure
/// and report per row" needs - noted in the change's design.md.)
type UpsertInvoice = ValidInvoice -> Result<StoredInvoice, InvoiceError>

type DeleteInvoice = InvoiceId -> Result<StoredInvoice option, InvoiceError>

type LoadTombstones = unit -> Result<InvoiceTombstone list, InvoiceError>
type SaveTombstone = InvoiceTombstone -> Result<unit, InvoiceError>
type RemoveTombstone = SupplierId -> InvoiceReference -> Result<bool, InvoiceError>

type LoadScanProblems = unit -> Result<ScanProblem list, InvoiceError>
type SaveScanProblems = ScanProblem list -> Result<unit, InvoiceError>
type ClearScanProblems = SourceMessageId list -> Result<unit, InvoiceError>

type LoadScanWindows = unit -> Result<StoredScanWindow list, InvoiceError>
type SaveScanWindow = ScanWindowDays -> Result<StoredScanWindow, InvoiceError>
type DeleteScanWindow = ScanWindowId -> Result<bool, InvoiceError>
type LoadSelectedScanWindow = unit -> Result<ScanWindowDays option, InvoiceError>
type SaveSelectedScanWindow = ScanWindowDays -> Result<unit, InvoiceError>
