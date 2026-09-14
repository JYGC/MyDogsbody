namespace MyDogsbody.Domain.Calendar

// The Google calendar workflow area: constrained primitives, one type per pipeline stage, the
// area's error DU, and the dependency function types its workflows declare.
//
// Created here with the accounts + calendars half (change #6, google-account-integration);
// change #7 (invoice-calendar-sync) extends it with the events half and the sync plan. Nothing
// here names CalendarService, an OAuth type, ILiteCollection or an HTTP type - the domain cannot
// reach any of them, and does not need to.

open MyDogsbody.Domain.Invoices

/// The identifier the store assigned. Opaque to the domain, the same way MailAccountId is.
type GoogleAccountId = private GoogleAccountId of string

module GoogleAccountId =

    let create (value: string) : Result<GoogleAccountId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Google account id must not be empty."
        else
            Ok (GoogleAccountId value)

    let value (GoogleAccountId id) = id

/// The account's own address at Google - what tells two registered accounts apart on screen.
type GoogleEmail = private GoogleEmail of string

module GoogleEmail =

    let create (value: string) : Result<GoogleEmail, string> =
        let trimmed = if isNull value then "" else value.Trim()

        if System.String.IsNullOrWhiteSpace trimmed then
            Error "Email address must not be empty."
        elif not (trimmed.Contains "@") then
            Error "Email address must contain '@'."
        else
            Ok (GoogleEmail trimmed)

    let value (GoogleEmail emailAddress) = emailAddress

/// A calendar's id at Google. Opaque - it is Google's business what shape it has.
type CalendarId = private CalendarId of string

module CalendarId =

    let create (value: string) : Result<CalendarId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Calendar id must not be empty."
        else
            Ok (CalendarId value)

    let value (CalendarId id) = id

/// A calendar's display name, as Google shows it.
type CalendarName = private CalendarName of string

module CalendarName =

    let create (value: string) : Result<CalendarName, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Calendar name must not be empty."
        else
            Ok (CalendarName value)

    let value (CalendarName name) = name

/// A calendar as Google lists it. The domain carries only what a person picks from.
type AvailableCalendar =
    {
        Id: CalendarId
        Name: CalendarName
        IsPrimary: bool
    }

/// A registered account, carrying its default invoice calendar - Q2.3.
///
/// DefaultInvoiceCalendar is not laziness: a freshly authorised account genuinely has no
/// calendar chosen yet, and per Q2.11 that state renders as NOT READY with the sync action
/// disabled, rather than as a failure at the API.
type RegisteredGoogleAccount =
    {
        Id: GoogleAccountId
        EmailAddress: GoogleEmail
        DefaultInvoiceCalendar: CalendarId option
        NeedsReauthorisation: bool
    }

// ---------------------------------------------------------------------------------------------
// Change #7 (invoice-calendar-sync) - the events half. AllDayEvent and CalendarEventId are
// defined here, ahead of CalendarError, because EventNoLongerExists (added to CalendarError
// below) carries a CalendarEventId.
// ---------------------------------------------------------------------------------------------

/// Start date, title and description of a calendar event for an invoice. No time, no time zone,
/// no duration - Q2.1 makes every invoice event all-day on the due date, so the domain carries
/// nothing it never sets, and the mapper cannot accidentally invent one.
type AllDayEvent = { Date: System.DateTime; Title: string; Description: string }

/// A calendar event's id at Google. Opaque - it is Google's business what shape it has.
type CalendarEventId = private CalendarEventId of string

module CalendarEventId =

    let create (value: string) : Result<CalendarEventId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Calendar event id must not be empty."
        else
            Ok(CalendarEventId value)

    let value (CalendarEventId id) = id

/// What can go wrong in this area, in terms a person could say out loud. Each case carries the
/// values its message is written from.
///
/// GoogleAccountIdInvalid and CalendarIdInvalid are not in design.md's original listing - its
/// workflow signatures take raw strings (`accountId: string`, `calendarId: string`) but its
/// CalendarError has no case for one that is malformed rather than merely unknown. Adding them
/// here is the same fix MailAccountsTypes.MailAccountIdInvalid made for the identical gap.
///
/// Change #7 (invoice-calendar-sync) adds EventRejected, EventNoLongerExists and the rest, for
/// the events half.
type CalendarError =
    | ClientSecretMissing
    | ClientSecretInvalid of reason: string
    | AuthorisationCancelled
    | AuthorisationFailed of reason: string
    | AccountAlreadyRegistered of GoogleEmail
    | AccountNotRegistered of GoogleAccountId
    | AccountEmailUnavailable
    | NotAuthorised of GoogleAccountId
    /// The Google Cloud project behind the client secret has never had the Calendar API enabled
    /// (Google's own `accessNotConfigured`). Its own case rather than folded into `NotAuthorised`
    /// for exactly the reason `CalendarRateLimited` is separate: re-authorising can never fix it,
    /// so an alert telling the user to re-authorise sends them round a loop that cannot end. The
    /// payload carries Google's own sentence, which names the project and the URL that enables it.
    | CalendarApiNotEnabled of message: string
    | CalendarUnreachable of message: string
    | CalendarRateLimited of message: string
    | CalendarNoLongerExists of CalendarId
    | NoDefaultCalendar of GoogleAccountId
    | GoogleStoreFailed of message: string
    | GoogleAccountIdInvalid of reason: string
    | CalendarIdInvalid of reason: string
    /// Change #7: Google rejected the event Google itself is being asked to create or update -
    /// an invalid title, date or property value. Logged once; there is no remedy but reporting
    /// which row failed and why.
    | EventRejected of reason: string
    /// Change #7: an update or delete named an event Google no longer has - deleted by hand, or
    /// on a calendar that has since been removed. Expected and NOT a failure: the calendar
    /// already agrees with the target state, so the caller treats this as AlreadyGone, a
    /// success, rather than as a batch-stopping error.
    | EventNoLongerExists of CalendarEventId

// Dependencies as function types - not interfaces, not classes, not a collection getter. A
// workflow receives a function value, so a test supplies a lambda and the composition root
// supplies the real adapter. The domain names no CalendarService, OAuth type or HTTP type.

type LoadClientSecret = unit -> Result<string option, CalendarError>
type SaveClientSecret = string -> Result<unit, CalendarError>
/// Completing consent persists a token before the account's email is known. An `Error` carries no
/// id, so `DiscardAuthorisation` cannot be pointed at that token: an implementation that fails
/// after consent hands its own token back before returning.
type AuthoriseAccount = unit -> Result<GoogleEmail * GoogleAccountId, CalendarError>
type ListGoogleAccounts = unit -> Result<RegisteredGoogleAccount list, CalendarError>
type SaveGoogleAccount = RegisteredGoogleAccount -> Result<RegisteredGoogleAccount, CalendarError>
type RemoveGoogleAccount = GoogleAccountId -> Result<bool, CalendarError>
/// The calendars an account can add events to - never one it can only read, since every calendar
/// this returns is one the default-invoice-calendar picker offers and `SetDefaultInvoiceCalendar`
/// accepts.
type ListCalendars = GoogleAccountId -> Result<AvailableCalendar list, CalendarError>

/// Throws away an authorisation that was completed but never became a registered account.
///
/// `AuthoriseAccount` cannot be undone by not saving: completing consent has already persisted a
/// token against the id it returns, before this workflow gets to decide whether the registration
/// is allowed. So a refused registration owes a discard, or the token is left behind with no
/// account row pointing at it - unreachable by removal, and durable until someone revokes it at
/// Google.
type DiscardAuthorisation = GoogleAccountId -> Result<unit, CalendarError>

/// Re-authorises an *existing* account, reusing its id rather than minting a new one - the
/// analogue of `AuthoriseAccount` for the "token expired or revoked" path.
///
/// Not in design.md's original listing: `GoogleAccountApi.ReauthoriseAccount` is specified there,
/// but no dependency type or workflow backs it. Added here for the same reason
/// `GoogleAccountIdInvalid`/`CalendarIdInvalid` were - a gap between a specified UI-facing
/// operation and the domain types that would need to exist for it to be a real workflow rather
/// than composition-root logic.
type ReauthoriseAccount = GoogleAccountId -> Result<GoogleEmail, CalendarError>

// ---------------------------------------------------------------------------------------------
// Change #7 (invoice-calendar-sync) - events, the sync plan, and their dependency function
// types. InvoiceSyncKey.fs (compiled just before this file) supplies the one derivation; nothing
// here re-derives it.
// ---------------------------------------------------------------------------------------------

/// An event as read back from Google. SyncKey is None when the extended property is absent or
/// unparseable - which is an orphan needing attention, never a deletion candidate.
type CalendarEvent =
    { Id: CalendarEventId
      Event: AllDayEvent
      SyncKey: InvoiceSyncKey option }

/// The bound `ListCalendarEvents` needs.
///
/// Q2.5 ties it to the scan window, which needs saying carefully because the obvious reading is
/// wrong: the scan window looks BACKWARDS at when mail arrived, while an invoice event sits on
/// its DUE date, which is normally ahead of that. Querying [today - N, today] would miss the
/// event for every invoice not yet due, read it as absent, and create a second one. Both ends are
/// dates - time of day discarded - because every invoice event is all-day.
type CalendarDateRange = private CalendarDateRange of System.DateTime * System.DateTime

module CalendarDateRange =

    let create (startDate: System.DateTime) (endDate: System.DateTime) : Result<CalendarDateRange, string> =
        if startDate.Date > endDate.Date then
            Error "A calendar date range's start must not be after its end."
        else
            Ok(CalendarDateRange(startDate.Date, endDate.Date))

    let startDate (CalendarDateRange(startDate, _)) = startDate
    let endDate (CalendarDateRange(_, endDate)) = endDate

/// What Q2.6 turns the diff into.
type SyncAction =
    | CreateEvent of UploadableInvoice
    /// The event disagrees with the ledger - title, date, or both.
    | UpdateEvent of CalendarEventId * UploadableInvoice
    /// The invoice is GONE FROM THE LEDGER - not merely outside the window. See LedgerSnapshot.
    | DeleteEvent of CalendarEventId * InvoiceSyncKey
    /// Identical - no API call at all.
    | LeaveAlone of CalendarEventId

/// The input to the diff, and hazard (a)'s structural guard.
///
/// AllLedgerKeys is EVERY key in the ledger, windowed or not. InWindow is what the page is
/// showing. A DeleteEvent is produced only for an event whose key is absent from AllLedgerKeys -
/// so "outside the window" and "gone from the ledger" cannot be confused, because the diff is
/// never handed a windowed-only list in the first place.
type LedgerSnapshot =
    { InWindow: UploadableInvoice list
      AllLedgerKeys: Set<InvoiceSyncKey> }

/// One invoice and the event id it is synced to.
type SyncedInvoice = { Invoice: UploadableInvoice; EventId: CalendarEventId }

/// What executing one SyncAction actually did.
type SyncOutcome =
    | Created of InvoiceId * CalendarEventId
    | Updated of InvoiceId * CalendarEventId
    | Deleted of CalendarEventId
    /// LeaveAlone executed - no call was made.
    | Skipped of CalendarEventId
    /// An update or delete found the event already gone. A success: the calendar already agrees
    /// with the target state.
    | AlreadyGone of CalendarEventId
    | Failed of SyncAction * CalendarError

/// Bounded by a CalendarDateRange and filtered to events carrying the app's own private extended
/// property (Q2.4) - an event added by hand comes back with SyncKey = None rather than being
/// excluded, so the diff (never this type) is what decides it is an orphan.
type ListCalendarEvents =
    GoogleAccountId -> CalendarId -> CalendarDateRange -> Result<CalendarEvent list, CalendarError>

/// Creates an all-day event and stamps the extended property with the derived InvoiceSyncKey.
type CreateCalendarEvent =
    GoogleAccountId -> CalendarId -> InvoiceSyncKey -> AllDayEvent -> Result<CalendarEventId, CalendarError>

/// Rewrites title and date unconditionally (Q2.14) - the event is app-owned and always wins.
type UpdateCalendarEvent =
    GoogleAccountId -> CalendarId -> CalendarEventId -> AllDayEvent -> Result<unit, CalendarError>

type DeleteCalendarEvent =
    GoogleAccountId -> CalendarId -> CalendarEventId -> Result<unit, CalendarError>

/// Records (or updates) which event an invoice is synced to, on the InvoiceCalendarEvents table -
/// history, not truth. The calendar remains the source of truth for the diff.
type MarkSynced =
    InvoiceId -> GoogleAccountId -> CalendarId -> CalendarEventId -> Result<unit, InvoiceError>

/// Removes an invoice's sync record after its event is deleted.
type ClearSyncRecord = InvoiceId -> Result<unit, InvoiceError>

/// Every InvoiceSyncKey the ledger currently holds, ignoring any scan window - hazard (a)'s
/// guard depends on this being unwindowed. Never call LoadInvoices and derive keys from a
/// windowed result in its place.
type LoadAllLedgerKeys = unit -> Result<Set<InvoiceSyncKey>, InvoiceError>
