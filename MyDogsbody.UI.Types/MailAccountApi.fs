namespace MyDogsbody.UI.Types

open MyDogsbody.Exceptions.Types

/// The whole surface the UI is allowed to reach for Thunderbird mail accounts. A record of
/// functions rather than an interface: there is one implementation, built by partial
/// application at startup, and a test substitutes it by building a record literal.
///
/// A write to GetAccounts' backing store reloads on the next GetAccounts call - the UI decides
/// when to reload, same shape as CredentialApi/SupplierApi.
type MailAccountApi =
    {
        GetProfileRoot: unit -> Result<string option, MyDogsbodyException>
        SetProfileRoot: string -> Result<unit, MyDogsbodyException>
        ScanForAccounts: unit -> Result<DiscoveryResultUiType, MyDogsbodyException>
        GetAccounts: unit -> Result<MailAccountUiType list * string option, MyDogsbodyException>
        SelectAccount: string -> Result<unit, MyDogsbodyException>
        CountMessages: string -> Result<int, MyDogsbodyException>
        ClearWatermarks: string -> Result<unit, MyDogsbodyException>
    }
