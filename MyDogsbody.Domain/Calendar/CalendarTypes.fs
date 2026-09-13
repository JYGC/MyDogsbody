namespace MyDogsbody.Domain.Calendar

// The Google calendar workflow area: constrained primitives, one type per pipeline stage, the
// area's error DU, and the dependency function types its workflows declare.
//
// Created here with the accounts + calendars half (change #6, google-account-integration);
// change #7 (invoice-calendar-sync) extends it with the events half and the sync plan. Nothing
// here names CalendarService, an OAuth type, ILiteCollection or an HTTP type - the domain cannot
// reach any of them, and does not need to.

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

    let value (GoogleEmail e) = e

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

    let value (CalendarName n) = n

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
