module MyDogsbody.Domain.Calendar.SetClientSecretWorkflow

open MyDogsbody.Domain.Calendar

/// Stores the application-wide OAuth client secret.
///
/// Refuses a blank one without reaching the store. The page, and `RegisterGoogleAccountWorkflow`'s
/// "no client secret" check, both read a stored secret as one that has been supplied - so a blank
/// save read back as a secret: "Add account" enabled, and registering failed at the authorisation
/// call as "malformed", which requirements.md's "say so and disable account registration" rules
/// out. Refusing it here also keeps a working secret from being replaced by nothing.
///
/// Anything else is stored exactly as given. Whether it parses is the adapter's to report, when it
/// is used ("The stored Google client secret is malformed.").
let setClientSecret (saveClientSecret: SaveClientSecret) (secret: string) : Result<unit, CalendarError> =
    if System.String.IsNullOrWhiteSpace secret then
        Error(ClientSecretInvalid "Google client secret must not be empty.")
    else
        saveClientSecret secret
