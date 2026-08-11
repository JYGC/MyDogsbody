module MyDogsbody.Domain.Invoices.MatchSupplierWorkflow

open System
open MyDogsbody.Domain.Suppliers

let private senderDomain (sender: string) : string =
    match sender.IndexOf '@' with
    | -1 -> ""
    | index -> sender.Substring(index + 1)

let private matches (message: ScannedMessage) (matcher: SupplierMatcher) : bool =
    match matcher with
    | SenderAddress address -> String.Equals(address, message.Sender, StringComparison.OrdinalIgnoreCase)
    | SenderDomain domain -> String.Equals(domain, senderDomain message.Sender, StringComparison.OrdinalIgnoreCase)
    | SubjectPattern substring -> message.Subject.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0

/// Matches a message against the stored suppliers, treating a supplier's rules as alternatives -
/// any one rule matching is enough. Pure: no I/O, the caller already loaded the suppliers.
let matchSupplier (suppliers: StoredSupplier list) (message: ScannedMessage) : Result<SupplierId, InvoiceError> =
    let matchedSupplierIds =
        suppliers
        |> List.filter (fun supplier -> supplier.Matchers |> List.exists (matches message))
        |> List.map (fun supplier -> supplier.Id)

    match matchedSupplierIds with
    | [ single ] -> Ok single
    | [] -> Error (SupplierNotRecognised message.Sender)
    | multiple -> Error (MultipleSuppliersMatched(message.Sender, multiple))
