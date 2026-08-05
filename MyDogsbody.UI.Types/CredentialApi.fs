namespace MyDogsbody.UI.Types

open MyDogsbody.Exceptions.Types

/// The whole surface the UI is allowed to reach. A record of functions rather than an
/// interface: there is one implementation, built by partial application at startup, and a
/// test substitutes it by building a record literal.
///
/// Every member returns Result — the composition root does not collapse failures, so the
/// UI is the layer that decides what a failure looks like on screen.
type CredentialApi =
    {
        GetAllCredentials: unit -> Result<IntegrationCredentialUiType list, MyDogsbodyException>
        AddCredential: IntegrationCredentialUiTypeWithoutId -> Result<unit, MyDogsbodyException>
        EditCredential: IntegrationCredentialUiType -> Result<unit, MyDogsbodyException>
    }
