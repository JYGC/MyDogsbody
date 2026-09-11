/// The Google Calendar adapter. Only `listCalendars` is declared and bound in this change
/// (design decision 3) - `ListCalendarEvents`, `CreateCalendarEvent`, `UpdateCalendarEvent` and
/// `DeleteCalendarEvent` arrive in change #7 with the workflows that consume them. A dependency
/// function type is a published interface owing a contract suite, and a suite for a type no
/// workflow consumes is a suite written against a guess.
///
/// Calls are blocked on (`Async.RunSynchronously`) rather than made properly asynchronous in
/// this change (friction #1) - calls already run off the render thread via `startWork`. Revisit
/// if change #7's batch of calendar-event calls makes the interface feel stuck.
module MyDogsbody.Integrations.Google.GoogleCalendarClient

open System
open System.Net
open Google.Apis.Auth.OAuth2
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.Calendar.v3
open Google.Apis.Http
open Google.Apis.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar

/// The stable openings `GoogleAccountApiMappers.toListCalendarsError` matches on. The rest of
/// each message is Google's own text, which carries the detail a user needs (the project id and
/// the URL that enables the API, in the not-enabled case) - so the mapper matches a prefix rather
/// than the whole sentence.
let apiNotEnabledPrefix = "The Google Calendar API is not enabled for this project."
let unreachablePrefix = "Could not reach Google Calendar."

/// Google reports *why* a 403 happened in `error.errors[].reason`; `accessNotConfigured` (the API
/// is switched off for the project) and `insufficientPermissions` (the granted scopes are too
/// narrow) both arrive as a bare 403 and need opposite responses from the user.
let private hasReason (reason: string) (ex: Google.GoogleApiException) =
    match ex.Error with
    | null -> false
    | error ->
        match error.Errors with
        | null -> false
        | errors -> errors |> Seq.exists (fun e -> e.Reason = reason)

/// Google's documented usage-limit reasons (the Calendar API's "Handle API errors" guide). Each can
/// arrive as a 403, not only as a 429 - and a 403 is otherwise read as a permission failure. The
/// guide's remedy for all of them is to back off and retry; re-authorising fixes none of them.
let private usageLimitReasons =
    [ "rateLimitExceeded"; "userRateLimitExceeded"; "quotaExceeded"; "dailyLimitExceeded" ]

let private isUsageLimit (ex: Google.GoogleApiException) =
    usageLimitReasons |> List.exists (fun reason -> hasReason reason ex)

let private googleMessage (ex: Google.GoogleApiException) =
    match ex.Error with
    | null -> ex.Message
    | error when String.IsNullOrWhiteSpace error.Message -> ex.Message
    | error -> error.Message

/// A calendar entry the domain's rules reject (an empty id or name, which Google never actually
/// sends) is dropped rather than failing the whole list - the same "be lenient reading, strict
/// writing" posture the rest of this codebase's bottom mappers take.
let private toAvailableCalendar (entry: Data.CalendarListEntry) : AvailableCalendar option =
    match CalendarId.create entry.Id, CalendarName.create entry.Summary with
    | Ok id, Ok name -> Some { Id = id; Name = name; IsPrimary = entry.Primary.GetValueOrDefault() }
    | _ -> None

/// Follows `nextPageToken` until Google reports there is no more - task 4.1's paged-list test is
/// what proves this rather than returning only the first page.
let private listAllPages (service: CalendarService) : Data.CalendarListEntry list =
    let rec loop (pageToken: string) (acc: Data.CalendarListEntry list) =
        let request = service.CalendarList.List()

        if not (String.IsNullOrEmpty pageToken) then
            request.PageToken <- pageToken

        let page = request.Execute()
        let items = if isNull page.Items then [] else List.ofSeq page.Items
        let combined = acc @ items

        if String.IsNullOrEmpty page.NextPageToken then
            combined
        else
            loop page.NextPageToken combined

    loop null []

/// The seam `listCalendars` closes over: an explicit `IHttpClientFactory` lets a test construct
/// `CalendarService` over a stubbed `HttpMessageHandler` rather than a real network connection.
let listCalendarsVia
    (httpClientFactory: IHttpClientFactory option)
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    ()
    : Result<AvailableCalendar list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars

    handleError {
        try
            let initializer =
                BaseClientService.Initializer(HttpClientInitializer = credential, ApplicationName = "MyDogsbody")

            httpClientFactory |> Option.iter (fun factory -> initializer.HttpClientFactory <- factory)

            use service = new CalendarService(initializer)

            return listAllPages service |> List.choose toAvailableCalendar
        with
        | :? Google.GoogleApiException as ex when hasReason "accessNotConfigured" ex ->
            // A 403 that re-authorising can never fix: the Cloud project behind the client secret
            // has the Calendar API switched off. Google's own sentence names the project and the
            // URL that enables it, so it is carried through verbatim rather than replaced by a
            // message of ours that would send the user to do the wrong thing.
            return! MyDogsbodyException(action, $"{apiNotEnabledPrefix} {googleMessage ex}", ex)
        // Ahead of the 401/403 clause below, which would otherwise take a usage-limit 403 and tell a
        // rate-limited user to re-authorise - the instruction design decision 7 exists to prevent.
        | :? Google.GoogleApiException as ex when
            ex.HttpStatusCode = HttpStatusCode.TooManyRequests
            || (ex.HttpStatusCode = HttpStatusCode.Forbidden && isUsageLimit ex)
            ->
            return! MyDogsbodyException(action, "Google is rate-limiting this account; try again shortly.", ex)
        | :? Google.GoogleApiException as ex when
            ex.HttpStatusCode = HttpStatusCode.Unauthorized || ex.HttpStatusCode = HttpStatusCode.Forbidden
            ->
            return! MyDogsbodyException(action, "The stored Google credential is no longer authorised.", ex)
        // Not an answer from the Calendar API at all: the credential tried to refresh its access
        // token and Google's token endpoint refused the refresh token - "invalid_grant", "Token has
        // been expired or revoked." That is requirements.md's "a stored token has expired or been
        // revoked", and while a refresh token is stored it arrives this way rather than as a 401 from
        // the call itself (the credential answers a 401 by refreshing). It is also the common case: a
        // Testing-mode OAuth client's refresh tokens last seven days. Without this clause it fell
        // to the catch-all below and read as "Could not reach Google Calendar." - a revoked grant
        // reported as a network problem. Google.Apis.Auth has already deleted the stored token by
        // the time this runs, so re-authorising is the only remedy left, and the one reported.
        //
        // Only invalid_grant: the token endpoint's other refusals (invalid_client,
        // unauthorized_client) implicate the client secret rather than this account's grant, and
        // keep the catch-all with Google's code appended.
        | :? TokenResponseException as ex when not (isNull ex.Error) && ex.Error.Error = "invalid_grant" ->
            return! MyDogsbodyException(action, "The stored Google credential is no longer authorised.", ex)
        | ex ->
            // Google's own text appended, because "could not reach" on its own leaves a user with
            // nothing to act on - the real reason was thrown away before it reached the screen.
            return! MyDogsbodyException(action, $"{unreachablePrefix} {ex.Message}", ex)
    }

/// The composition root's entry point - the default `HttpClientFactory`.
let listCalendars
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    ()
    : Result<AvailableCalendar list, MyDogsbodyException> =
    listCalendarsVia None handleError credential ()
