module MyDogsbody.Domain.Credentials.ListCredentialsWorkflow

open MyDogsbody.Domain.Credentials

/// Lists the stored credentials.
///
/// Deliberately thin: there is no rule to apply today, so it validates nothing and sorts
/// nothing - the table shows what the store returned, in the order it returned it. It exists so
/// the read path has the same shape as the write paths, and so the first rule that does arrive
/// (hiding revoked credentials, a defined sort order) has one obvious place to land. Binding
/// LoadCredentials straight to the API record would save this file at the cost of putting a use
/// case in the composition root.
let listCredentials
    (loadCredentials: LoadCredentials)
    ()
    : Result<StoredCredential list, CredentialError> =
    loadCredentials ()
