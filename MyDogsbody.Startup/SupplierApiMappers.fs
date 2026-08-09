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
open MyDogsbody.Domain
open MyDogsbody.Domain.Suppliers
open MyDogsbody.UI.Types

/// UI string -> domain union.
///
/// Returns Result rather than failing loudly the way CredentialApiMappers.toInfrastructure does.
/// That mapper is handed InfrastructureType, a C# enum the UI can only produce declared members
/// of; this one is handed a plain string, so an unrecognised value is reachable input rather than
/// an impossible one. SupplierApi promises Result<_, MyDogsbodyException>, and raising here broke
/// that promise in the one place nothing catches it - the UI calls the API from Async.Start, so
/// the exception surfaced as neither an alert nor a log entry.
let private toMatcherKind (kind: string) : Result<MatcherKind, string> =
    match kind with
    | "Sender" -> Ok Sender
    | "Domain" -> Ok Domain
    | "Subject" -> Ok Subject
    | unknown -> Error $"Matcher kind '{unknown}' has no domain equivalent."

/// Domain union -> UI string. Exhaustive: adding a case to MatcherKind breaks this build.
let private toMatcherKindUiString (kind: MatcherKind) : string =
    match kind with
    | Sender -> "Sender"
    | Domain -> "Domain"
    | Subject -> "Subject"

/// Stops at the first unrecognised kind, the same way the domain's own matcher validation stops
/// at the first invalid rule.
let private toUnvalidatedMatchers
    (matchers: SupplierMatcherUiType list)
    : Result<(MatcherKind * string) list, SupplierError> =
    let rec loop remaining acc =
        match remaining with
        | [] -> Ok (List.rev acc)
        | (m: SupplierMatcherUiType) :: rest ->
            match toMatcherKind m.Kind with
            | Error reason -> Error (MatcherInvalid reason)
            | Ok kind -> loop rest ((kind, m.Value) :: acc)

    loop matchers []

let private toMatcherUiType (matcher: SupplierMatcher) : SupplierMatcherUiType =
    {
        Kind = toMatcherKindUiString (SupplierMatcher.kind matcher)
        Value = SupplierMatcher.value matcher
    }

let toUnvalidatedSupplier
    (uiType: SupplierUiTypeWithoutId)
    : Result<UnvalidatedSupplier, SupplierError> =
    result {
        let! matchers = toUnvalidatedMatchers uiType.Matchers

        return
            {
                Name = uiType.Name
                PaymentTermDays = uiType.PaymentTermDays
                Matchers = matchers
            }
    }

let toUnvalidatedSupplierEdit
    (uiType: SupplierUiType)
    : Result<UnvalidatedSupplierEdit, SupplierError> =
    result {
        let! matchers = toUnvalidatedMatchers uiType.Matchers

        return
            {
                Id = uiType.Id
                Name = uiType.Name
                PaymentTermDays = uiType.PaymentTermDays
                Matchers = matchers
            }
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
/// Nothing here logs, in either branch: this value is constructed inside Result.mapError and
/// returned straight to the UI, never inside a handleError block, so writeLog is never reached.
/// A store failure has already been logged once by the adapter's own handleError, which is why
/// SupplierStoreFailed must not be logged again here.
///
/// The ApplicationException on the expected cases is therefore belt-and-braces rather than the
/// active mechanism: it marks them for ExceptionHelpers.isApplicationException should a caller
/// ever put an API call inside a handleError block. SupplierStoreFailed is left unmarked because
/// it is the one case that genuinely was an exception.
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
