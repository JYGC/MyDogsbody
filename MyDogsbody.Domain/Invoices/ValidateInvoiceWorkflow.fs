/// Turns change #2's parsed values (ExtractedInvoice - plain string, unconstrained decimal) into
/// the constrained ValidInvoice the ledger stores. Pure: no I/O, no clock.
module MyDogsbody.Domain.Invoices.ValidateInvoiceWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices

/// An implausible issue or due date (year outside InvoiceIssueDate's guard) is dropped rather
/// than failing the whole invoice: the date is optional (Q1.10), and the record of the invoice
/// survives whether or not every field does. A genuine parse failure was already an InvoiceError
/// back in ApplyTemplateWorkflow; by here the value is a DateTime.
let private optionalIssueDate (value: System.DateTime option) : InvoiceIssueDate option =
    value
    |> Option.bind (fun dt ->
        match InvoiceIssueDate.create dt with
        | Ok date -> Some date
        | Error _ -> None)

let private optionalDueDate (value: System.DateTime option) : InvoiceDueDate option =
    value
    |> Option.bind (fun dt ->
        match InvoiceDueDate.create dt with
        | Ok date -> Some date
        | Error _ -> None)

/// requirements.md: a supplier, an invoice reference, an amount and a currency are required; a
/// missing due date is fine. A value that fails validation returns its own InvoiceError case
/// carrying the raw value the message is written from (task 3.3).
let validateInvoice
    (messageReceivedAt: System.DateTime)
    (extracted: ExtractedInvoice)
    : Result<ValidInvoice, InvoiceError> =
    result {
        let! reference =
            InvoiceReference.create extracted.Reference
            |> Result.mapError (fun _ -> InvoiceReferenceInvalid extracted.Reference)

        let! amount =
            Money.create extracted.Amount extracted.Currency
            |> Result.mapError (fun _ ->
                if System.String.IsNullOrWhiteSpace extracted.Currency then
                    CurrencyInvalid extracted.Currency
                else
                    AmountInvalid(string extracted.Amount))

        return
            { SupplierId = extracted.SupplierId
              TemplateId = extracted.TemplateId
              SourceMessageId = extracted.SourceMessageId
              Reference = reference
              Amount = amount
              IssueDate = optionalIssueDate extracted.IssueDate
              DueDate = optionalDueDate extracted.DueDate
              MessageReceivedAt = messageReceivedAt }
    }
