namespace MyDogsbody.UI.Types

open MyDogsbody.Exceptions.Types

/// The whole surface the UI is allowed to reach for Google accounts. A record of functions
/// rather than an interface: one implementation, built by partial application at startup, and a
/// test substitutes it with a record literal - the same shape as `SupplierApi`/`MailAccountApi`.
///
/// A write reloads on the caller's next `GetAccounts` call, same convention as every other API
/// record in this codebase - the UI decides when to reload.
type GoogleAccountApi =
    {
        /// The application-wide client secret, if one has been supplied. requirements.md: shown
        /// read-only on the page once set, with an "Edit" button that reveals it, pre-filled, for
        /// changing - never asked for again from a blank field.
        GetClientSecret: unit -> Result<string option, MyDogsbodyException>
        SetClientSecret: string -> Result<unit, MyDogsbodyException>
        GetAccounts: unit -> Result<GoogleAccountUiType list, MyDogsbodyException>
        RegisterAccount: unit -> Result<GoogleAccountUiType, MyDogsbodyException>
        ReauthoriseAccount: string -> Result<GoogleAccountUiType, MyDogsbodyException>
        RemoveAccount: string -> Result<unit, MyDogsbodyException>
        GetCalendarsFor: string -> Result<CalendarUiType list, MyDogsbodyException>
        /// Account id -> calendar id -> the updated account, with its `DefaultInvoiceCalendarId`
        /// set.
        SetDefaultInvoiceCalendar: string -> string -> Result<GoogleAccountUiType, MyDogsbodyException>
    }
