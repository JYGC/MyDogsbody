namespace MyDogsbody.Domain.Credentials

// The credentials workflow area: constrained primitives, one type per pipeline stage, the
// area's error DU, and the dependency function types its workflows declare.
//
// Nothing here names MyDogsbodyException, HandleErrorBuilder, ILiteCollection or
// InfrastructureType. The domain cannot reach any of them, and does not need to.

/// The infrastructure a credential belongs to.
///
/// Deliberately the domain's own union rather than MyDogsbody.Enums.InfrastructureType: the
/// domain references no project at all, so the enum is unreachable here. Both edge mappers
/// translate it with an exhaustive match, which makes adding a member to one and not the other
/// a compile error rather than a runtime surprise.
type Infrastructure =
    | Google
    | Microsoft

/// The secret itself - an API key, a refresh token, a JSON blob. Stored and returned exactly as
/// entered; nothing here trims or re-encodes it.
type CredentialSecret = private CredentialSecret of string

module CredentialSecret =

    let create (value: string) : Result<CredentialSecret, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Credentials must not be empty."
        else
            Ok (CredentialSecret value)

    let value (CredentialSecret secret) = secret

/// The username the credential authenticates as, at the external service. Named for what it is
/// rather than for the UI's "Username", and the store persists it under this name.
type ExternalUsername = private ExternalUsername of string

module ExternalUsername =

    let create (value: string) : Result<ExternalUsername, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Username must not be empty."
        else
            Ok (ExternalUsername value)

    let value (ExternalUsername username) = username

/// The identifier the store assigned. Opaque to the domain - it is the store's business what
/// shape it has, and the domain only ever compares and carries it.
type CredentialId = private CredentialId of string

module CredentialId =

    let create (value: string) : Result<CredentialId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Credential id must not be empty."
        else
            Ok (CredentialId value)

    let value (CredentialId id) = id

// A type per stage, so code holding one cannot be handed another.

/// What the dialog produced. Untrusted - the two free-text fields are plain strings because
/// nothing has checked them yet.
type UnvalidatedCredential =
    {
        Infrastructure: Infrastructure
        Credentials: string
        ExternalUsername: string
    }

/// An edit as submitted. Carries the identifier of the row it means to change.
type UnvalidatedCredentialEdit =
    {
        Id: string
        Infrastructure: Infrastructure
        Credentials: string
        ExternalUsername: string
    }

/// Been through validation. Holding this type is the proof - nothing downstream re-checks.
type ValidCredential =
    {
        Infrastructure: Infrastructure
        Credentials: CredentialSecret
        ExternalUsername: ExternalUsername
    }

/// A validated intent to change an existing row.
///
/// Structurally identical to StoredCredential today, and kept separate on purpose: this is an
/// intent to write, that is a fact read back. Collapsing them would let a function that wants
/// one accept the other.
type ValidCredentialEdit =
    {
        Id: CredentialId
        Infrastructure: Infrastructure
        Credentials: CredentialSecret
        ExternalUsername: ExternalUsername
    }

/// Been through the store.
type StoredCredential =
    {
        Id: CredentialId
        Infrastructure: Infrastructure
        Credentials: CredentialSecret
        ExternalUsername: ExternalUsername
    }

/// What can go wrong in this area, in terms a person could say out loud. Each case carries the
/// values its message is written from.
///
/// Deliberately short: bugs, violated invariants and infrastructure collapse are not here. They
/// stay exceptions, get caught at the outer-ring boundary, and arrive with a stack trace worth
/// reading. CredentialStoreFailed is the one exception-shaped case, and it exists because the
/// composition root needs somewhere to put an adapter failure the user must still be told about.
type CredentialError =
    | CredentialsInvalid of reason: string
    | ExternalUsernameInvalid of reason: string
    | CredentialIdInvalid of reason: string
    | CredentialNotFound of CredentialId
    | CredentialStoreFailed of message: string

// Dependencies as function types - not interfaces, not classes, not a collection getter. A
// workflow receives a function value, so a test supplies a lambda and the composition root
// supplies the real adapter.

type LoadCredentials = unit -> Result<StoredCredential list, CredentialError>

type SaveCredential = ValidCredential -> Result<StoredCredential, CredentialError>

/// Returns None when no row carried that identifier, so "not found" stays a decision the
/// workflow makes rather than one the adapter makes for it.
type UpdateCredential = ValidCredentialEdit -> Result<StoredCredential option, CredentialError>
