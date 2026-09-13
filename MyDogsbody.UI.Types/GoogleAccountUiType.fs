namespace MyDogsbody.UI.Types

/// A calendar as Google lists it, for the per-account picker (Q2.3).
type CalendarUiType =
    {
        Id: string
        Name: string
        IsPrimary: bool
    }

/// A registered Google account. `DefaultInvoiceCalendarId = None` means the account genuinely
/// has none chosen yet - Q2.11's NOT READY state - not a loading placeholder.
type GoogleAccountUiType =
    {
        Id: string
        EmailAddress: string
        DefaultInvoiceCalendarId: string option
        NeedsReauthorisation: bool
    }
