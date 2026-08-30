/// Removes an invoice from the ledger by hand (Q5.12: delete yes, edit no) and writes a
/// tombstone on its natural key so the next scan does not put it back (Q5.14). Delete first,
/// then tombstone - a tombstone for a row that was never removed would hide an invoice the user
/// did not delete.
module MyDogsbody.Domain.Invoices.DeleteInvoiceWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Invoices

let deleteInvoice
    (deleteFromLedger: DeleteInvoice)
    (saveTombstone: SaveTombstone)
    (getCurrentTime: GetCurrentTime)
    (rawInvoiceId: string)
    : Result<unit, InvoiceError> =
    result {
        let! invoiceId =
            InvoiceId.create rawInvoiceId |> Result.mapError (fun _ -> InvoiceNotFound)

        let! removed = deleteFromLedger invoiceId

        match removed with
        | None ->
            // Already gone: nothing to delete and - importantly - no tombstone written.
            return! Error InvoiceNotFound
        | Some stored ->
            return!
                saveTombstone
                    { SupplierId = stored.Invoice.SupplierId
                      Reference = stored.Invoice.Reference
                      DeletedAt = getCurrentTime () }
    }
