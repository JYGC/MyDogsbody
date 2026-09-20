namespace MyDogsbody.UI.Types

open System

/// Supplier is a NAME here (the top mapper joins the supplier list), not an id - the screen never
/// sees a domain id.
type InvoiceUiType =
    { Id: string
      SupplierName: string
      Reference: string
      Amount: decimal
      Currency: string
      IssueDate: DateTime option
      DueDate: DateTime option
      MessageReceivedAt: DateTime
      /// True when the invoice has a due date and can become a calendar event (change #7).
      CanBecomeCalendarEvent: bool
      /// The sentence shown when it cannot - "No due date was found, so this invoice cannot be
      /// added to a calendar." None when it can.
      CannotUploadReason: string option
      /// Change #7: the per-row calendar sync state (design decision 5) - up to date, missing or
      /// changed. Always None from InvoiceApi.GetInvoices itself, which knows nothing about
      /// Google; the page overlays this from InvoiceSyncApi.GetSyncPlan's StatusByInvoiceId, and
      /// it stays None for an invoice InvoiceSyncApi has no opinion about - in particular any
      /// invoice with CanBecomeCalendarEvent = false, which InvoiceSyncApi never classifies.
      SyncStatus: InvoiceSyncStatusUiType option }

/// A message that yielded no invoice, as the problems view renders it - a message id alone is
/// not actionable, so the sender, subject and date travel with the cause sentence.
type ScanProblemUiType =
    { SourceMessageId: string
      Sender: string
      Subject: string
      ReceivedAt: DateTime
      Cause: string
      RecordedAt: DateTime }

/// A hand-deleted invoice, as the tombstones view renders it, with an un-delete. SupplierId is
/// the opaque row key the un-delete call needs (like InvoiceUiType.Id, a string, not a domain
/// type); the screen shows SupplierName.
type TombstoneUiType =
    { SupplierId: string
      SupplierName: string
      Reference: string
      DeletedAt: DateTime }

/// Label is composed by the top mapper - "mail received in the last 90 days", never a bare "90"
/// (Q1.6).
type ScanWindowUiType = { Id: string; Days: int; Label: string }

type ScanResultUiType =
    { Invoices: InvoiceUiType list
      Problems: ScanProblemUiType list }
