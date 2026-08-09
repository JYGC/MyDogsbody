/// The top mapping point: domain type <-> MyDogsbody.UI.Types record, plus the translation
/// between the two error types.
///
/// A deliberate cost, same as CredentialApiMappers: a workflow's StoredSupplier could be handed
/// to the UI directly, saving this file - but that would put MyDogsbody.Domain in UI.Portal's
/// reference graph. Keeping the UI on its own records is what makes the domain unreachable from
/// the screen rather than merely unused there.
///
/// Total functions with no module-level bindings, so a test reaches them without Startup.fs
/// opening a database.
module MyDogsbody.Startup.SupplierApiMappers

open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Suppliers
open MyDogsbody.UI.Types

/// UI string -> domain union. The UI's dropdown only ever offers the three known kinds, so an
/// unrecognised value here is a bug rather than something a user did - the same idiom
/// CredentialApiMappers.toInfrastructure uses for InfrastructureType.
let private toMatcherKind (kind: string) : MatcherKind =
    match kind with
    | "Sender" -> Sender
    | "Domain" -> Domain
    | "Subject" -> Subject
    | unknown -> failwith $"Matcher kind '{unknown}' has no domain equivalent."

/// Domain union -> UI string. Exhaustive: adding a case to MatcherKind breaks this build.
let private toMatcherKindUiString (kind: MatcherKind) : string =
    match kind with
    | Sender -> "Sender"
    | Domain -> "Domain"
    | Subject -> "Subject"

let private toUnvalidatedMatchers
    (matchers: SupplierMatcherUiType list)
    : (MatcherKind * string) list =
    matchers |> List.map (fun m -> toMatcherKind m.Kind, m.Value)

let private toMatcherUiType (matcher: SupplierMatcher) : SupplierMatcherUiType =
    {
        Kind = toMatcherKindUiString (SupplierMatcher.kind matcher)
        Value = SupplierMatcher.value matcher
    }

let toUnvalidatedSupplier (uiType: SupplierUiTypeWithoutId) : UnvalidatedSupplier =
    {
        Name = uiType.Name
        PaymentTermDays = uiType.PaymentTermDays
        Matchers = toUnvalidatedMatchers uiType.Matchers
    }

let toUnvalidatedSupplierEdit (uiType: SupplierUiType) : UnvalidatedSupplierEdit =
    {
        Id = uiType.Id
        Name = uiType.Name
        PaymentTermDays = uiType.PaymentTermDays
        Matchers = toUnvalidatedMatchers uiType.Matchers
    }

let toUiType (stored: StoredSupplier) : SupplierUiType =
    {
        Id = SupplierId.value stored.Id
        Name = SupplierName.value stored.Name
        PaymentTermDays = PaymentTermDays.value stored.PaymentTermDays
        Matchers = stored.Matchers |> List.map toMatcherUiType
    }

/// Outbound: a domain error case becomes the exception the UI renders as a sentence.
///
/// Every case except SupplierStoreFailed wraps an ApplicationException, so handleError's
/// TryWith passes it through unlogged - the same idiom PdfDocumentReader.readContent uses for a
/// missing file. SupplierStoreFailed is the one exception-shaped case: it wraps the store's own
/// message with no ApplicationException marker, so it is logged like any other unexpected
/// failure.
let toMyDogsbodyException (action: string) (error: SupplierError) : MyDogsbodyException =
    let expected (message: string) =
        MyDogsbodyException(action, message, System.ApplicationException message)

    match error with
    | SupplierNameInvalid reason -> expected reason
    | PaymentTermInvalid reason -> expected reason
    | SupplierNameTaken name -> expected $"The supplier name '{name}' is already in use."
    | MatcherInvalid reason -> expected reason
    | SupplierIdInvalid reason -> expected reason
    | SupplierNotFound id -> expected $"No supplier was found with id '{SupplierId.value id}'."
    | SupplierStoreFailed message -> MyDogsbodyException(action, message)

/// Inbound: an adapter's exception becomes the one domain case that stands for infrastructure
/// failure. The adapter's handleError has already logged it, so nothing logs again here.
let toSupplierError (ex: MyDogsbodyException) : SupplierError =
    SupplierStoreFailed ex.Message
