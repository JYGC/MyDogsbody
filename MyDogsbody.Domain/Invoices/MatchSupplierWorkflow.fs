module MyDogsbody.Domain.Invoices.MatchSupplierWorkflow

open System
open MyDogsbody.Domain.Suppliers

/// Sender and Subject are unconstrained strings an outer-ring adapter fills in, and a mail store
/// with no subject hands over null rather than "". TextNormalization.normalizeText guards the
/// same way for the same reason: an exception raised here would unwind out of the domain, past a
/// composition root that maps values rather than catching, into a UI with no alert for it -
/// which CLAUDE.md rules out in either ring.
let private orEmpty (text: string) : string =
    if isNull text then "" else text

/// The address out of a mail header. Change #3 hands over whatever the mail store gives it, which
/// is very often a full "Display Name <address>" header rather than a bare address; everything
/// between the last '<' and the last '>' is the address, and anything else is taken as already
/// being one.
let private addressOf (sender: string) : string =
    let trimmed = (orEmpty sender).Trim()
    let opening = trimmed.LastIndexOf '<'
    let closing = trimmed.LastIndexOf '>'

    if opening >= 0 && closing > opening then
        trimmed.Substring(opening + 1, closing - opening - 1).Trim()
    else
        trimmed

/// The domain is the part after the LAST '@' of the address, not the first. A quoted local part
/// legally carries its own ("a@b"@acme.example), and splitting on the first '@' also left the
/// trailing '>' on the domain of every display-name sender - so SenderDomain "acme.example" did
/// not match, SenderAddress did not match either, and the supplier fell through to
/// SupplierNotRecognised entirely.
let private senderDomain (sender: string) : string =
    let address = addressOf sender

    match address.LastIndexOf '@' with
    | -1 -> ""
    | index -> address.Substring(index + 1).Trim()

/// A matcher whose stored value is empty matches NOTHING. Without this, an empty domain matcher
/// compared equal to the "" that senderDomain yields for a message with no sender at all, so that
/// one supplier claimed every senderless message. SupplierMatcher.create now refuses to build one,
/// but rows saved before that still exist, so the workflow refuses to honour one too.
let private matchesValue (stored: string) (candidate: string) : bool =
    not (String.IsNullOrWhiteSpace stored)
    && String.Equals(stored, candidate, StringComparison.OrdinalIgnoreCase)

let private matches (message: ScannedMessage) (matcher: SupplierMatcher) : bool =
    match matcher with
    | SenderAddress address -> matchesValue address (addressOf message.Sender)
    | SenderDomain domain -> matchesValue domain (senderDomain message.Sender)
    | SubjectPattern substring ->
        // A subject is text a rule is evaluated against, so requirements.md's "WHEN any rule is
        // evaluated THE SYSTEM SHALL first apply a defined normalization to the text" covers it.
        // The stored value is a plain substring rather than a regex, so it is normalized too -
        // safe here in a way it would not be for a pattern, and it closes the other half of the
        // same gap: a subject fragment pasted out of a real mail carries that mail's spaces.
        not (String.IsNullOrWhiteSpace substring)
        && (InvoiceText.normalizeLine (orEmpty message.Subject))
            .IndexOf(InvoiceText.normalizeLine substring, StringComparison.OrdinalIgnoreCase) >= 0

/// Matches a message against the stored suppliers, treating a supplier's rules as alternatives -
/// any one rule matching is enough. Pure: no I/O, the caller already loaded the suppliers.
let matchSupplier (suppliers: StoredSupplier list) (message: ScannedMessage) : Result<SupplierId, InvoiceError> =
    let matchedSupplierIds =
        suppliers
        |> List.filter (fun supplier -> supplier.Matchers |> List.exists (matches message))
        |> List.map (fun supplier -> supplier.Id)

    match matchedSupplierIds with
    | [ single ] -> Ok single
    | [] -> Error (SupplierNotRecognised(orEmpty message.Sender))
    | multiple -> Error (MultipleSuppliersMatched(orEmpty message.Sender, multiple))
