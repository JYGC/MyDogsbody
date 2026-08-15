namespace MyDogsbody.Domain.Invoices

open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

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
