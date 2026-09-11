/// The top mapping point: domain type <-> MyDogsbody.UI.Types record, plus the translation
/// between the two error types.
///
/// Total functions with no module-level bindings, so a test reaches them without Startup.fs
/// opening a database.
module MyDogsbody.Startup.GoogleAccountApiMappers

open System
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.UI.Types

// ---------- domain type -> UI record ----------

let toCalendarUiType (calendar: AvailableCalendar) : CalendarUiType =
    {
        Id = CalendarId.value calendar.Id
        Name = CalendarName.value calendar.Name
        IsPrimary = calendar.IsPrimary
    }

let toGoogleAccountUiType (account: RegisteredGoogleAccount) : GoogleAccountUiType =
    {
        Id = GoogleAccountId.value account.Id
        EmailAddress = GoogleEmail.value account.EmailAddress
        DefaultInvoiceCalendarId = account.DefaultInvoiceCalendar |> Option.map CalendarId.value
        NeedsReauthorisation = account.NeedsReauthorisation
    }

// ---------- inbound: adapter exception -> CalendarError ----------
//
// Grouped by which adapter action produced the exception, matching design.md's own ActionNames
// grouping. Message strings are exact literals the adapter itself chose - see tasks.md phases 3
// and 4 for the tables both adapters were built against.

/// `GoogleAuthorization.authorise`/`.reauthorise` failures. The one case genuinely needing
/// context beyond the exception - `NotAuthorised` - never comes from here: authorise/reauthorise
/// only ever run for an account whose identity is being established or re-established, so
/// "not authorised" for THIS call has no separate meaning from the call simply failing.
let toAuthorisationError (ex: MyDogsbodyException) : CalendarError =
    match ex.Message with
    | "The consent flow was cancelled or denied." -> AuthorisationCancelled
    | "The stored Google client secret is malformed." -> ClientSecretInvalid ex.Message
    | "The authorised account's email address could not be read." -> AccountEmailUnavailable
    // The two failures GoogleAuthorization names for itself keep the sentence it chose. The
    // catch-all below prefers the inner exception because "Authorisation failed." carries
    // nothing - but for these two the inner exception carries *less* than the adapter's own
    // wording: HttpListenerException talks about listener prefixes without ever saying
    // "loopback" or "port", and a cancelled consent flow arrives as a bare "The operation was
    // canceled." / "A task was canceled." Both are what requirements.md asks be reported
    // specifically and with a reason, so neither may be replaced by the exception underneath.
    // The full exception is still logged by the adapter's own handleError.
    | "The loopback port is already in use."
    | "The consent flow timed out." -> AuthorisationFailed ex.Message
    // A consent completed without the calendar scope. Same reasoning: the sentence carries the
    // remedy, while the inner exception only lists the scopes that were granted.
    | "Google Calendar access was not granted - tick the calendar permission on Google's consent screen and try again." ->
        AuthorisationFailed ex.Message
    | _ ->
        let reason = match ex.InnerException with null -> ex.Message | inner -> inner.Message
        AuthorisationFailed reason

/// `GoogleCalendarClient.listCalendars` failures for a specific account. `NotAuthorised` needs
/// the account id, which only this call site has in scope - `GoogleAuthorization.loadCredential`
/// also reports "no stored credential" through the same message, ahead of ever reaching Google.
let toListCalendarsError (accountId: GoogleAccountId) (ex: MyDogsbodyException) : CalendarError =
    // The two messages that carry Google's own text are matched by their stable opening rather
    // than in full - see GoogleCalendarClient's `apiNotEnabledPrefix` / `unreachablePrefix`.
    if ex.Message.StartsWith GoogleCalendarClient.apiNotEnabledPrefix then
        CalendarApiNotEnabled ex.Message
    else
        match ex.Message with
        | "The stored Google credential is no longer authorised."
        | "No stored credential for this account." -> NotAuthorised accountId
        | "Google is rate-limiting this account; try again shortly." -> CalendarRateLimited ex.Message
        // `loadCredential` parses the stored client secret before anything is sent to Google, so
        // a malformed one is neither "unreachable" nor "not authorised" - and it is the user's to
        // fix by re-pasting, which is advice neither of the other two cases would give them.
        | "The stored Google client secret is malformed." -> ClientSecretInvalid ex.Message
        | _ -> CalendarUnreachable ex.Message

/// `GoogleAccountStore` failures (the client secret and the account rows) - always
/// infrastructure, never something a user did.
let toStoreError (ex: MyDogsbodyException) : CalendarError = GoogleStoreFailed ex.Message

// ---------- outbound: CalendarError -> MyDogsbodyException ----------

/// Expected failures - wrapped in an `ApplicationException` and passed unlogged, the same
/// convention `MailAccountApiMappers.toMyDogsbodyException` uses. `AuthorisationFailed`,
/// `CalendarUnreachable`, `CalendarRateLimited` and `GoogleStoreFailed` are the ones that were
/// genuinely infrastructure trouble - the adapter that produced them has already logged once via
/// `handleError`, so this translation does not log again either way.
let toMyDogsbodyException (action: string) (error: CalendarError) : MyDogsbodyException =
    let expected (message: string) = MyDogsbodyException(action, message, ApplicationException message)

    match error with
    | ClientSecretMissing -> expected "No Google client secret has been supplied yet."
    | ClientSecretInvalid reason -> expected reason
    | AuthorisationCancelled -> expected "The consent flow was cancelled or denied."
    | AccountAlreadyRegistered email -> expected $"The account '{GoogleEmail.value email}' is already registered."
    | AccountNotRegistered id -> expected $"No Google account was found with id '{GoogleAccountId.value id}'."
    | AccountEmailUnavailable -> expected "The authorised account's email address could not be read."
    | NotAuthorised id -> expected $"The account '{GoogleAccountId.value id}' needs to be re-authorised."
    | NoDefaultCalendar id ->
        expected $"The account '{GoogleAccountId.value id}' has no default calendar chosen yet."
    | CalendarNoLongerExists calendarId ->
        expected $"The calendar '{CalendarId.value calendarId}' no longer exists."
    | GoogleAccountIdInvalid reason -> expected reason
    | CalendarIdInvalid reason -> expected reason
    | AuthorisationFailed reason -> MyDogsbodyException(action, reason)
    // Expected, and the message carries the remedy (the project id and the URL that enables the
    // API) - a configuration state the user can fix, not a defect worth a stack trace here. The
    // adapter has already logged it once with the full exception.
    | CalendarApiNotEnabled message -> expected message
    | CalendarUnreachable message -> MyDogsbodyException(action, message)
    | CalendarRateLimited message -> MyDogsbodyException(action, message)
    | GoogleStoreFailed message -> MyDogsbodyException(action, message)
