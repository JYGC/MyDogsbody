/// The top mapping point: domain type <-> MyDogsbody.UI.Types record, plus the translation
/// between the two error types.
///
/// This mapper is a deliberate cost. A workflow's StoredCredential could be handed to the UI
/// directly, saving the file - but that would put MyDogsbody.Domain in UI.Portal's reference
/// graph. Keeping the UI on its own records is what makes the domain unreachable from the
/// screen rather than merely unused there.
///
/// Total functions with no module-level bindings, so a test reaches them without Startup.fs
/// opening a database.
module MyDogsbody.Startup.CredentialApiMappers

open MyDogsbody.Enums
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Credentials
open MyDogsbody.UI.Types

/// UI enumeration -> domain union.
///
/// InfrastructureType is a C# enum, so the compiler cannot prove this exhaustive - it can hold
/// any integer. An unrecognised value is a bug rather than something a user did, so it fails
/// loudly; CredentialApiMappersTests walks every declared member to catch a mismatch first.
let private toInfrastructure (infrastructureType: InfrastructureType) : Infrastructure =
    match infrastructureType with
    | InfrastructureType.Google -> Google
    | InfrastructureType.Microsoft -> Microsoft
    | unknown -> failwith $"InfrastructureType '{unknown}' has no domain equivalent."

/// Domain union -> UI enumeration. Exhaustive: adding a case to Infrastructure breaks this build.
let private toInfrastructureType (infrastructure: Infrastructure) : InfrastructureType =
    match infrastructure with
    | Google -> InfrastructureType.Google
    | Microsoft -> InfrastructureType.Microsoft

let toUnvalidatedCredential (uiType: IntegrationCredentialUiTypeWithoutId) : UnvalidatedCredential =
    {
        Infrastructure = toInfrastructure uiType.InfrastructureType
        Credentials = uiType.Credentials
        // Deliberate rename: the UI calls it Username, the domain and the store call it what it
        // is - the username at the external service.
        ExternalUsername = uiType.Username
    }

let toUnvalidatedCredentialEdit (uiType: IntegrationCredentialUiType) : UnvalidatedCredentialEdit =
    {
        Id = uiType.Id
        Infrastructure = toInfrastructure uiType.InfrastructureType
        Credentials = uiType.Credentials
        ExternalUsername = uiType.Username
    }

let toUiType (stored: StoredCredential) : IntegrationCredentialUiType =
    {
        Id = CredentialId.value stored.Id
        InfrastructureType = toInfrastructureType stored.Infrastructure
        Credentials = CredentialSecret.value stored.Credentials
        Username = ExternalUsername.value stored.ExternalUsername
    }

/// Outbound: a domain error case becomes the exception the UI renders as a sentence.
///
/// No inner exception - a domain error never was one. That also keeps it away from writeLog:
/// this value is constructed and returned directly, never inside a handleError block, so an
/// expected failure reaches the user without a row landing in the exception log.
let toMyDogsbodyException (action: string) (error: CredentialError) : MyDogsbodyException =
    let message =
        match error with
        | CredentialsInvalid reason -> reason
        | ExternalUsernameInvalid reason -> reason
        | CredentialIdInvalid reason -> reason
        | CredentialNotFound id -> $"No credential was found with id '{CredentialId.value id}'."
        | CredentialStoreFailed message -> message

    MyDogsbodyException(action, message)

/// Inbound: an adapter's exception becomes the one domain case that stands for infrastructure
/// failure. The adapter's handleError has already logged it, so nothing logs again here.
let toCredentialError (ex: MyDogsbodyException) : CredentialError =
    CredentialStoreFailed ex.Message
