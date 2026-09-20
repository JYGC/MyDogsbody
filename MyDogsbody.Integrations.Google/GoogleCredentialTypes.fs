namespace MyDogsbody.Integrations.Google

// These live in the integration, NOT in MyDogsbody.Domain. Q3.7: a credential is a token an
// adapter presents - there is no rule to express and no decision a workflow makes about one. The
// only check, "the secret is non-empty", is the adapter's own precondition. If a future workflow
// ever genuinely reasons about a credential, that is when a domain type earns its place.
//
// The reason strings match the ones the retired shared store used, so the characterization
// assertions carried over from Phase 1 keep passing word for word.

/// The secret itself - an API key, an OAuth refresh token, a JSON blob. Stored and returned
/// exactly as entered; nothing here trims or re-encodes it.
type GoogleCredentialSecret = private GoogleCredentialSecret of string

module GoogleCredentialSecret =

    let create (value: string) : Result<GoogleCredentialSecret, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Credentials must not be empty."
        else
            Ok (GoogleCredentialSecret value)

    let value (GoogleCredentialSecret secret) = secret

/// The username the credential authenticates as, at Google.
type GoogleExternalUsername = private GoogleExternalUsername of string

module GoogleExternalUsername =

    let create (value: string) : Result<GoogleExternalUsername, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Username must not be empty."
        else
            Ok (GoogleExternalUsername value)

    let value (GoogleExternalUsername username) = username

/// The identifier the store assigned. Opaque - it is the store's business what shape it has.
type GoogleCredentialId = private GoogleCredentialId of string

module GoogleCredentialId =

    let create (value: string) : Result<GoogleCredentialId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Credential id must not be empty."
        else
            Ok (GoogleCredentialId value)

    let value (GoogleCredentialId id) = id

type ValidGoogleCredential =
    {
        Secret: GoogleCredentialSecret
        Username: GoogleExternalUsername
    }

type ValidGoogleCredentialEdit =
    {
        Id: GoogleCredentialId
        Secret: GoogleCredentialSecret
        Username: GoogleExternalUsername
    }

type StoredGoogleCredential =
    {
        Id: GoogleCredentialId
        Secret: GoogleCredentialSecret
        Username: GoogleExternalUsername
    }
