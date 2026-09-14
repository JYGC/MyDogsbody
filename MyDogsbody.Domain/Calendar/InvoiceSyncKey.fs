namespace MyDogsbody.Domain.Calendar

open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices

/// The idempotency key. Supplier + invoice reference - the natural key from the ledger, NOT the
/// database id, so rebuilding the ledger from scratch does not make every event read as missing.
///
/// It identifies THREE things: a row in the ledger's unique index, an event on a calendar, and a
/// tombstone. ONE function derives it and everything else calls that function; three hand-rolled
/// derivations would agree right up until one of them did not.
type InvoiceSyncKey = private InvoiceSyncKey of string

module InvoiceSyncKey =

    /// The private extended property's name on a Google Calendar event.
    [<Literal>]
    let PropertyName = "mydogsbody.invoice"

    /// ASCII Unit Separator (0x1F) - the same character InvoiceRecordMappers already uses to
    /// fold more than one field into one persisted string, and for the same reason: it does not
    /// occur in a supplier's row id or in a normalized invoice reference.
    let private fieldSeparator = char 0x1F

    let derive (supplierId: SupplierId) (reference: InvoiceReference) : InvoiceSyncKey =
        InvoiceSyncKey $"{SupplierId.value supplierId}{fieldSeparator}{InvoiceReference.value reference}"

    let parse (value: string) : Result<InvoiceSyncKey, string> =
        if System.String.IsNullOrEmpty value then
            Error "Invoice sync key must not be empty."
        else
            match value.Split(fieldSeparator) with
            | [| supplierIdPart; referencePart |] when supplierIdPart <> "" && referencePart <> "" ->
                Ok(InvoiceSyncKey value)
            | _ -> Error $"'{value}' is not a valid invoice sync key."

    let value (InvoiceSyncKey key) = key
