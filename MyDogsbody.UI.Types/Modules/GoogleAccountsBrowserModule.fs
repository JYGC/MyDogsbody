namespace MyDogsbody.UI.Types.Module

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types

type GoogleAccountsBrowserModule =
    {
        /// The stored client secret, if any - shown read-only on the page until
        /// `StartEditingClientSecret` is called (requirements.md: never open for editing by
        /// default).
        ClientSecretAval: aval<string option>
        /// Whether the client secret field is currently open for editing.
        IsEditingClientSecretAval: aval<bool>
        AccountsAval: aval<GoogleAccountUiType list>
        /// Calendars fetched per account, keyed by account id - only populated once loaded, so
        /// a picker with nothing loaded yet renders empty rather than stale.
        CalendarsByAccountIdAval: aval<Map<string, CalendarUiType list>>
        IsLoadingAval: aval<bool>
        /// The consent flow is running - shown, but never blocks the interface
        /// (requirements.md: "SHALL NOT block the user interface").
        IsRegisteringAval: aval<bool>
        /// The message from the last failed operation, cleared by the next successful one.
        ErrorAval: aval<string option>
        /// Reveals the client secret field for editing, pre-filled with the currently stored
        /// value (requirements.md: "a correction does not require retyping the whole secret").
        StartEditingClientSecret: unit -> unit
        /// Closes the editable field without saving, leaving the stored value untouched.
        CancelEditingClientSecret: unit -> unit
        SetClientSecret: string -> unit
        RegisterAccount: unit -> unit
        ReauthoriseAccount: string -> unit
        RemoveAccount: string -> unit
        SetDefaultInvoiceCalendar: string -> string -> unit
    }
