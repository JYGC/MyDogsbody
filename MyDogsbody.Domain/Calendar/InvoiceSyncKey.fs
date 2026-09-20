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

    [<Literal>]
    let PrivateExtendedPropertyNameOnAGoogleCalendarEvent = "mydogsbody.invoice"

    /// The same character InvoiceRecordMappers already uses to fold more than one field into one
    /// persisted string, and for the same reason: it does not occur in a supplier's row id or in
    /// a normalized invoice reference.
    let private asciiUnitSeparatorBetweenKeyParts = char 0x1F

    let derive (supplierId: SupplierId) (reference: InvoiceReference) : InvoiceSyncKey =
        let supplierRowId = SupplierId.value supplierId
        let invoiceReference = InvoiceReference.value reference

        InvoiceSyncKey $"{supplierRowId}{asciiUnitSeparatorBetweenKeyParts}{invoiceReference}"

    let parse (value: string) : Result<InvoiceSyncKey, string> =
        if System.String.IsNullOrEmpty value then
            Error "Invoice sync key must not be empty."
        else
            match value.Split(asciiUnitSeparatorBetweenKeyParts) with
            | [| supplierIdPart; referencePart |] when supplierIdPart <> "" && referencePart <> "" ->
                Ok(InvoiceSyncKey value)
            | _ -> Error $"'{value}' is not a valid invoice sync key."

    let value (InvoiceSyncKey key) = key

    /// This is the only way to say which invoice a DeleteEvent's event belonged to (design
    /// decision 3): the invoice itself is gone from the ledger by the time a delete is produced, so
    /// nothing else carries its identity. `None` only for a key this module did not itself derive
    /// or successfully parse, which cannot happen for a value that reached this function through
    /// `derive` or `parse`.
    let supplierRowIdAndInvoiceReferenceAsPlainStrings (InvoiceSyncKey key) : (string * string) option =
        match key.Split(asciiUnitSeparatorBetweenKeyParts) with
        | [| supplierIdPart; referencePart |] -> Some(supplierIdPart, referencePart)
        | _ -> None
