namespace MyDogsbody.Domain.Suppliers

// The suppliers workflow area: constrained primitives, one type per pipeline stage, the area's
// error DU, and the dependency function types its workflows declare.
//
// Nothing here names MyDogsbodyException, HandleErrorBuilder, QuerySource, SqliteConnection or
// Dapper. The domain cannot reach any of them, and does not need to.

/// The identifier the store assigned. Opaque to the domain - it is the store's business what
/// shape it has, the same way CredentialId is.
type SupplierId = private SupplierId of string

module SupplierId =

    let create (value: string) : Result<SupplierId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Supplier id must not be empty."
        else
            Ok (SupplierId value)

    let value (SupplierId id) = id

/// Trimmed on the way in, compared case-insensitively for uniqueness. Two spellings that differ
/// only by case or surrounding space are the same supplier - see requirements.md -> Edge cases.
type SupplierName = private SupplierName of string

module SupplierName =

    [<Literal>]
    let MaximumLength = 200

    let create (value: string) : Result<SupplierName, string> =
        let trimmed = if isNull value then "" else value.Trim()

        if System.String.IsNullOrWhiteSpace trimmed then
            Error "Supplier name must not be empty."
        elif trimmed.Length > MaximumLength then
            Error $"Supplier name must be {MaximumLength} characters or fewer."
        else
            Ok (SupplierName trimmed)

    let value (SupplierName name) = name

/// How long after issue this supplier's invoices fall due. A supplier-level fact for the same
/// reason the matcher is one: "Acme bills net 30" is about Acme, not about a document.
/// Nothing in this change reads it - DateFromField in change #2 is what it exists for.
type PaymentTermDays = private PaymentTermDays of int

module PaymentTermDays =

    [<Literal>]
    let Minimum = 0 // due on issue is a real term

    [<Literal>]
    let Maximum = 365

    let create (days: int) : Result<PaymentTermDays, string> =
        if days < Minimum || days > Maximum then
            Error $"Payment term days must be between {Minimum} and {Maximum}."
        else
            Ok (PaymentTermDays days)

    let value (PaymentTermDays days) = days

/// Which shape a match rule takes. Exists so the UI record and the persisted row can carry a
/// kind without either of them naming the SupplierMatcher union - the same reason
/// Infrastructure exists in the credentials area today.
type MatcherKind =
    | Sender
    | Domain
    | Subject

/// How a message is recognised as this supplier's. Several per supplier, matching on any
/// (Q7.6.5). Kept on the supplier rather than the template: "is this mail from Acme?" is a fact
/// about Acme, while a template answers the different question "given it is Acme, where are the
/// fields?".
type SupplierMatcher =
    | SenderAddress of string // exact, case-insensitive
    | SenderDomain of string // the part after @, case-insensitive
    | SubjectPattern of string // case-insensitive substring - see design.md -> Decisions taken

module SupplierMatcher =

    [<Literal>]
    let MaximumValueLength = 400

    let private validateKind (kind: MatcherKind) (value: string) : Result<unit, string> =
        match kind with
        | Sender ->
            if value.Contains "@" then Ok () else Error "A sender address must contain '@'."
        | Domain ->
            // Emptiness is checked first and separately: an empty domain matcher used to save
            // cleanly, and MatchSupplierWorkflow reads "" as the domain of a message with no
            // sender at all, so that one matcher claimed every senderless message for its
            // supplier. Subject already had this rule; Sender gets it for free from the '@' test.
            if System.String.IsNullOrWhiteSpace value then
                Error "A sender domain must not be empty."
            elif value.Contains "@" then
                Error "A sender domain must not contain '@' - enter a domain, not an address."
            else
                Ok ()
        | Subject ->
            if System.String.IsNullOrWhiteSpace value then
                Error "A subject pattern must not be empty."
            else
                Ok ()

    /// Kind and raw value in, a validated matcher out. The kind is what decides which rule
    /// applies.
    let create (kind: MatcherKind) (rawValue: string) : Result<SupplierMatcher, string> =
        let value = if isNull rawValue then "" else rawValue

        match validateKind kind value with
        | Error reason -> Error reason
        | Ok () ->
            if value.Length > MaximumValueLength then
                Error $"Match values must be {MaximumValueLength} characters or fewer."
            else
                match kind with
                | Sender -> Ok (SenderAddress value)
                | Domain -> Ok (SenderDomain value)
                | Subject -> Ok (SubjectPattern value)

    let kind (matcher: SupplierMatcher) : MatcherKind =
        match matcher with
        | SenderAddress _ -> Sender
        | SenderDomain _ -> Domain
        | SubjectPattern _ -> Subject

    let value (matcher: SupplierMatcher) : string =
        match matcher with
        | SenderAddress v -> v
        | SenderDomain v -> v
        | SubjectPattern v -> v

// One type per pipeline stage.

/// What the dialog produced. Untrusted - nothing has checked any of this yet.
type UnvalidatedSupplier =
    {
        Name: string
        PaymentTermDays: int
        Matchers: (MatcherKind * string) list
    }

/// An edit as submitted. Carries the identifier of the row it means to change.
type UnvalidatedSupplierEdit =
    {
        Id: string
        Name: string
        PaymentTermDays: int
        Matchers: (MatcherKind * string) list
    }

/// Been through validation. Holding this type is the proof - nothing downstream re-checks.
type ValidSupplier =
    {
        Name: SupplierName
        PaymentTermDays: PaymentTermDays
        Matchers: SupplierMatcher list
    }

/// A validated intent to change an existing row.
type ValidSupplierEdit =
    {
        Id: SupplierId
        Name: SupplierName
        PaymentTermDays: PaymentTermDays
        Matchers: SupplierMatcher list
    }

/// Been through the store.
type StoredSupplier =
    {
        Id: SupplierId
        Name: SupplierName
        PaymentTermDays: PaymentTermDays
        Matchers: SupplierMatcher list
    }

/// What can go wrong in this area, in terms a person could say out loud. Each case carries the
/// values its message is written from.
///
/// PaymentTermInvalid is not in design.md's original listing - the design lists AddSupplierWorkflow
/// as validating a payment term but the error DU it specifies has no case for that failure. Adding
/// one here is the smallest fix: without it, a payment-term rejection would have to borrow
/// SupplierNameInvalid and render a misleading message.
type SupplierError =
    | SupplierNameInvalid of reason: string
    | PaymentTermInvalid of reason: string
    | SupplierNameTaken of name: string
    | MatcherInvalid of reason: string
    | SupplierIdInvalid of reason: string
    | SupplierNotFound of SupplierId
    | SupplierStoreFailed of message: string

// Dependencies as function types - not interfaces, not classes, not a collection getter. A
// workflow receives a function value, so a test supplies a lambda and the composition root
// supplies the real adapter.

type LoadSuppliers = unit -> Result<StoredSupplier list, SupplierError>

type SaveSupplier = ValidSupplier -> Result<StoredSupplier, SupplierError>

/// None when no row carried that identifier, so "not found" stays the workflow's decision.
type UpdateSupplier = ValidSupplierEdit -> Result<StoredSupplier option, SupplierError>

type DeleteSupplier = SupplierId -> Result<bool, SupplierError>
