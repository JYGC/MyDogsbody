/// The bottom mapping point for the account half: LiteDB entity (C#, ObjectId, nullable
/// strings) <-> the domain's `RegisteredGoogleAccount`.
///
/// Pure - no I/O, no handleError, no LiteDB calls. GoogleAccountStore does the talking; this
/// file only translates, so it can be asserted field-for-field without a database.
module MyDogsbody.Integrations.Google.GoogleEntityMappers

open LiteDB
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google.Database.Models

/// Persistence -> the domain type, whole row.
///
/// Returns Result because LiteDB is schemaless: a document written by an older build, or edited
/// by hand, can carry a null where a constrained type is required. The store turns a failure
/// here into a logged MyDogsbodyException.
let toRegisteredAccount (entity: GoogleAccountEntity) : Result<RegisteredGoogleAccount, string> =
    match GoogleAccountId.create (string entity.Id) with
    | Error reason -> Error reason
    | Ok id ->

    match GoogleEmail.create entity.EmailAddress with
    | Error reason -> Error reason
    | Ok email ->

    // A null or empty stored calendar id maps to None, and only that - Q2.11's "genuinely has
    // none yet" state, not a validation failure. The only rule CalendarId.create enforces is
    // non-empty, so once that has been checked here there is no non-empty value left that could
    // still fail it.
    let defaultCalendarResult =
        if System.String.IsNullOrWhiteSpace entity.DefaultInvoiceCalendarId then
            Ok None
        else
            CalendarId.create entity.DefaultInvoiceCalendarId |> Result.map Some

    match defaultCalendarResult with
    | Error reason -> Error reason
    | Ok defaultCalendar ->

    Ok
        {
            Id = id
            EmailAddress = email
            DefaultInvoiceCalendar = defaultCalendar
            NeedsReauthorisation = entity.NeedsReauthorisation
        }

/// The domain type -> persistence. `Id` is set explicitly rather than left for LiteDB to
/// assign - the account's id is minted by the authorisation adapter before the browser opens,
/// so the same id already used as the OAuth datastore key is the one this row is keyed by.
let toEntity (account: RegisteredGoogleAccount) : GoogleAccountEntity =
    GoogleAccountEntity(
        Id = ObjectId(GoogleAccountId.value account.Id),
        EmailAddress = GoogleEmail.value account.EmailAddress,
        DefaultInvoiceCalendarId = (account.DefaultInvoiceCalendar |> Option.map CalendarId.value |> Option.toObj),
        NeedsReauthorisation = account.NeedsReauthorisation
    )
