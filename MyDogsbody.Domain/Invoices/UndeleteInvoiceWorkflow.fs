/// Removes a tombstone so the next scan of a covering window stores that invoice again (Q5.14 -
/// tombstones are visible and reversible).
module MyDogsbody.Domain.Invoices.UndeleteInvoiceWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices

let undeleteInvoice
    (removeTombstone: RemoveTombstone)
    (supplierId: SupplierId)
    (reference: InvoiceReference)
    : Result<unit, InvoiceError> =
    result {
        let! aTombstoneExistedForThatKeyAndWasRemoved = removeTombstone supplierId reference

        if aTombstoneExistedForThatKeyAndWasRemoved then
            return ()
        else
            return! Error InvoiceNotFound
    }
