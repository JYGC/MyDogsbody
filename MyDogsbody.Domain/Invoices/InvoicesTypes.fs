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
type InvoiceError =
    | SupplierNotRecognised of sender: string
    | MultipleSuppliersMatched of sender: string * suppliers: SupplierId list
    | NoTemplateForSupplier of SupplierId
    | TemplateMatchedNothing of template: TemplateId * field: TargetField
    | AmountUnparseable of field: TargetField * raw: string
    | DateUnparseable of field: TargetField * raw: string * format: string
    | RuleTimedOut of template: TemplateId * field: TargetField
