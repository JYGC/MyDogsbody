/// Removes a tombstone so the next scan of a covering window stores that invoice again (Q5.14 -
/// tombstones are visible and reversible). Undeleting a key that has no tombstone is reported,
/// not silently ignored.
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
        let! removed = removeTombstone supplierId reference

        if removed then
            return ()
        else
            return! Error InvoiceNotFound
    }
