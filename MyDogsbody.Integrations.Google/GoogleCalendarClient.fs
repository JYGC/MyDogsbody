/// The Google Calendar adapter. `listCalendars` was declared and bound alone in change #6
/// (design decision 3), deferring `ListCalendarEvents`, `CreateCalendarEvent`,
/// `UpdateCalendarEvent` and `DeleteCalendarEvent` to change #7, with the workflows that consume
/// them - a dependency function type is a published interface owing a contract suite, and a
/// suite for a type no workflow consumes is a suite written against a guess.
///
/// Change #7 (invoice-calendar-sync) adds `listEvents`, `createEvent`, `updateEvent` and
/// `deleteEvent` below, in the same outer-ring `Result<'T, MyDogsbodyException>` shape as
/// `listCalendars`. Translating that exception into a `CalendarError` - including turning a 404
/// on an update or delete into `EventNoLongerExists`, a success rather than a failure - is the
/// composition root's job (`Startup/GoogleAccountApiMappers.fs`, a later phase), not this file's;
/// each new function signals what happened only through its `ActionName` and a stable message
/// literal (`eventNotFoundMessage`, `eventRejectedPrefix`, `calendarGoneMessage`) a later phase's
/// mapper matches on, the same way `apiNotEnabledPrefix` and `unreachablePrefix` already work for
/// `listCalendars`.
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

/// Change #7: the stable message an update or delete signals a 404 with. `SyncInvoicesToCalendarWorkflow`
/// (task 4.2) treats this as `AlreadyGone`, a success rather than a failure - the calendar already
/// agrees with the target state - so the composition root's mapper matches this exact string to
/// translate it into `CalendarError.EventNoLongerExists` rather than a generic failure.
let eventNotFoundMessage = "The calendar event no longer exists."

/// Change #7: the stable opening a create or update signals a 400 rejection with. Google's own
/// sentence is appended, the same way `apiNotEnabledPrefix`'s is, so the row that failed says why
/// rather than only that it did. The composition root's mapper matches this prefix to translate
/// it into `CalendarError.EventRejected`.
let eventRejectedPrefix = "Google rejected the calendar event."

/// Change #7: the stable message `listEvents` signals a 410 with - Google's answer once the
/// whole calendar (not merely one event on it) has been deleted. The composition root's mapper
/// matches this exact string to translate it into `CalendarError.CalendarNoLongerExists`.
let calendarGoneMessage = "The calendar no longer exists."

/// Google reports *why* a 403 happened in `error.errors[].reason`; `accessNotConfigured` (the API
/// is switched off for the project) and `insufficientPermissions` (the granted scopes are too
/// narrow) both arrive as a bare 403 and need opposite responses from the user.
let private hasReason (reason: string) (googleApiException: Google.GoogleApiException) =
    match googleApiException.Error with
    | null -> false
    | error ->
        match error.Errors with
        | null -> false
        | errors -> errors |> Seq.exists (fun apiErrorEntry -> apiErrorEntry.Reason = reason)

/// Google's documented usage-limit reasons (the Calendar API's "Handle API errors" guide). Each can
/// arrive as a 403, not only as a 429 - and a 403 is otherwise read as a permission failure. The
/// guide's remedy for all of them is to back off and retry; re-authorising fixes none of them.
let private usageLimitReasons =
    [ "rateLimitExceeded"; "userRateLimitExceeded"; "quotaExceeded"; "dailyLimitExceeded" ]

let private isUsageLimit (googleApiException: Google.GoogleApiException) =
    usageLimitReasons |> List.exists (fun reason -> hasReason reason googleApiException)

let private googleMessage (googleApiException: Google.GoogleApiException) =
    match googleApiException.Error with
    | null -> googleApiException.Message
    | error when String.IsNullOrWhiteSpace error.Message -> googleApiException.Message
    | error -> error.Message

/// A calendar entry the domain's rules reject (an empty id or name, which Google never actually
/// sends) is dropped rather than failing the whole list - the same "be lenient reading, strict
/// writing" posture the rest of this codebase's bottom mappers take.
///
/// Named the way the account's own calendar list names it, which is what Google Calendar shows the
/// user: `summaryOverride` when the account has renamed the calendar, its owner's `summary`
/// otherwise. Two people's shared calendars can both be titled "Invoices", and the account's own
/// names for them are what tell them apart in the picker.
let private toAvailableCalendar (entry: Data.CalendarListEntry) : AvailableCalendar option =
    let name =
        if String.IsNullOrWhiteSpace entry.SummaryOverride then
            entry.Summary
        else
            entry.SummaryOverride

    match CalendarId.create entry.Id, CalendarName.create name with
    | Ok id, Ok name -> Some { Id = id; Name = name; IsPrimary = entry.Primary.GetValueOrDefault() }
    | _ -> None

/// Follows `nextPageToken` until Google reports there is no more - task 4.1's paged-list test is
/// what proves this rather than returning only the first page.
///
/// Asks for calendars the account can add events to (writer or owner access), on every page's
/// request. Left unasked, Google's list also carries the ones it can only read - "Holidays in
/// Australia", "Birthdays", anything subscribed to - and one chosen as the default invoice calendar
/// would show the account Ready with a calendar no invoice event can ever be written to: the
/// mid-sync discovery design decision 6 checks at choosing time to prevent.
let private listAllPages (service: CalendarService) : Data.CalendarListEntry list =
    let rec loop (pageToken: string) (accumulatedEntries: Data.CalendarListEntry list) =
        let request = service.CalendarList.List()
        request.MinAccessRole <- CalendarListResource.ListRequest.MinAccessRoleEnum.Writer

        if not (String.IsNullOrEmpty pageToken) then
            request.PageToken <- pageToken

        let page = request.Execute()
        let items = if isNull page.Items then [] else List.ofSeq page.Items
        let combined = accumulatedEntries @ items

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
        | :? Google.GoogleApiException as caughtApiNotEnabledException when
            hasReason "accessNotConfigured" caughtApiNotEnabledException
            ->
            // A 403 that re-authorising can never fix: the Cloud project behind the client secret
            // has the Calendar API switched off. Google's own sentence names the project and the
            // URL that enables it, so it is carried through verbatim rather than replaced by a
            // message of ours that would send the user to do the wrong thing.
            return!
                MyDogsbodyException(
                    action,
                    $"{apiNotEnabledPrefix} {googleMessage caughtApiNotEnabledException}",
                    caughtApiNotEnabledException
                )
        // Ahead of the 401/403 clause below, which would otherwise take a usage-limit 403 and tell a
        // rate-limited user to re-authorise - the instruction design decision 7 exists to prevent.
        | :? Google.GoogleApiException as caughtUsageLimitException when
            caughtUsageLimitException.HttpStatusCode = HttpStatusCode.TooManyRequests
            || (caughtUsageLimitException.HttpStatusCode = HttpStatusCode.Forbidden && isUsageLimit caughtUsageLimitException)
            ->
            return!
                MyDogsbodyException(action, "Google is rate-limiting this account; try again shortly.", caughtUsageLimitException)
        | :? Google.GoogleApiException as caughtUnauthorizedException when
            caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Unauthorized
            || caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Forbidden
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtUnauthorizedException
                )
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
        | :? TokenResponseException as caughtInvalidGrantException when
            not (isNull caughtInvalidGrantException.Error) && caughtInvalidGrantException.Error.Error = "invalid_grant"
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtInvalidGrantException
                )
        | caughtException ->
            // Google's own text appended, because "could not reach" on its own leaves a user with
            // nothing to act on - the real reason was thrown away before it reached the screen.
            return! MyDogsbodyException(action, $"{unreachablePrefix} {caughtException.Message}", caughtException)
    }

/// The composition root's entry point - the default `HttpClientFactory`.
let listCalendars
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    ()
    : Result<AvailableCalendar list, MyDogsbodyException> =
    listCalendarsVia None handleError credential ()

// ---------------------------------------------------------------------------------------------
// Change #7 (invoice-calendar-sync) - the events half: listEvents, createEvent, updateEvent and
// deleteEvent. GoogleAccountId is not a parameter of any of these, the same way it is not a
// parameter of listCalendarsVia/listCalendars above: resolving an account id to its stored
// credential is the composition root's job, not this adapter's.
// ---------------------------------------------------------------------------------------------

/// Reads back an event's own `mydogsbody.invoice` private extended property. `None` covers both
/// "absent" (an event added by hand) and "present but unparseable" - both are orphans the diff
/// must never delete, and never a reason to fail the whole read.
let private toInvoiceSyncKey (googleEvent: Data.Event) : InvoiceSyncKey option =
    match googleEvent.ExtendedProperties with
    | null -> None
    | extendedProperties ->
        match extendedProperties.Private__ with
        | null -> None
        | privateProperties ->
            match privateProperties.TryGetValue InvoiceSyncKey.PropertyName with
            | true, storedPropertyValue ->
                match InvoiceSyncKey.parse storedPropertyValue with
                | Ok syncKey -> Some syncKey
                | Error _ -> None
            | false, _ -> None

/// The date component of whichever of `Date` (an all-day event, what every event this
/// application creates carries) or `DateTime` (a timed one - only ever an event added by hand)
/// the event actually has, so a hand-added timed event still comes back as an orphan rather than
/// being silently dropped from the read.
let private toEventStartDate (eventDateTime: Data.EventDateTime) : DateTime option =
    match eventDateTime with
    | null -> None
    | _ when not (isNull eventDateTime.Date) ->
        Some(DateTime.Parse(eventDateTime.Date, System.Globalization.CultureInfo.InvariantCulture))
    | _ when eventDateTime.DateTimeDateTimeOffset.HasValue -> Some eventDateTime.DateTimeDateTimeOffset.Value.Date
    | _ -> None

/// An event Google would never actually send with no id or no start (neither an all-day date nor
/// a timed one) is dropped rather than failing the whole read - the same lenient-reading posture
/// `toAvailableCalendar` above takes for a calendar entry.
let private toCalendarEvent (googleEvent: Data.Event) : CalendarEvent option =
    match CalendarEventId.create googleEvent.Id, toEventStartDate googleEvent.Start with
    | Ok eventId, Some startDate ->
        Some
            {
                Id = eventId
                Event =
                    {
                        Date = startDate
                        Title = (if isNull googleEvent.Summary then "" else googleEvent.Summary)
                        Description = (if isNull googleEvent.Description then "" else googleEvent.Description)
                    }
                SyncKey = toInvoiceSyncKey googleEvent
            }
    | _ -> None

/// Follows `nextPageToken` the same way `listAllPages` does for calendars - task 5.1's
/// paged-list test is what proves every page is actually read rather than only the first.
/// `SingleEvents` is set on every page's request so a recurring event's series master never
/// stands in for its instances; `TimeMin`/`TimeMax` bound the read to the caller's
/// `CalendarDateRange`.
let private listAllEventPages
    (service: CalendarService)
    (calendarId: string)
    (rangeStartInclusive: DateTimeOffset)
    (rangeEndExclusive: DateTimeOffset)
    : Data.Event list =
    let rec loop (pageToken: string) (accumulatedEvents: Data.Event list) =
        let request = service.Events.List(calendarId)
        request.TimeMinDateTimeOffset <- Nullable rangeStartInclusive
        request.TimeMaxDateTimeOffset <- Nullable rangeEndExclusive
        request.SingleEvents <- Nullable true

        if not (String.IsNullOrEmpty pageToken) then
            request.PageToken <- pageToken

        let page = request.Execute()
        let items = if isNull page.Items then [] else List.ofSeq page.Items
        let combined = accumulatedEvents @ items

        if String.IsNullOrEmpty page.NextPageToken then
            combined
        else
            loop page.NextPageToken combined

    loop null []

/// Builds the all-day `Start`/`End`/`Summary`/`Description` shape both `createEvent` and
/// `updateEvent` send - Q2.14's "rewrites title and date unconditionally". An all-day event's
/// `End.Date` is the day AFTER `Start.Date` - Google's own convention for a one-day all-day
/// event; the two being equal instead renders as a zero-length event.
let private buildAllDayGoogleEvent (allDayEvent: AllDayEvent) : Data.Event =
    let toEventDateTime (date: DateTime) =
        Data.EventDateTime(Date = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))

    Data.Event(
        Summary = allDayEvent.Title,
        Description = allDayEvent.Description,
        Start = toEventDateTime allDayEvent.Date,
        End = toEventDateTime (allDayEvent.Date.AddDays(1.0))
    )

/// The seam `listEvents` closes over - see `listCalendarsVia` above for why an explicit
/// `IHttpClientFactory` exists.
let listEventsVia
    (httpClientFactory: IHttpClientFactory option)
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    (calendarId: CalendarId)
    (dateRange: CalendarDateRange)
    : Result<CalendarEvent list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listEvents

    handleError {
        try
            let initializer =
                BaseClientService.Initializer(HttpClientInitializer = credential, ApplicationName = "MyDogsbody")

            httpClientFactory |> Option.iter (fun factory -> initializer.HttpClientFactory <- factory)

            use service = new CalendarService(initializer)

            // TimeMax is Google's own upper bound, EXCLUSIVE of an event's start time - so the
            // range's own endDate (inclusive, per CalendarDateRange) needs a day added, or an
            // invoice due exactly on the window's last day would read as outside it.
            let rangeStartInclusive = DateTimeOffset(CalendarDateRange.startDate dateRange, TimeSpan.Zero)
            let rangeEndExclusive = DateTimeOffset((CalendarDateRange.endDate dateRange).AddDays(1.0), TimeSpan.Zero)

            return
                listAllEventPages service (CalendarId.value calendarId) rangeStartInclusive rangeEndExclusive
                |> List.choose toCalendarEvent
        with
        | :? Google.GoogleApiException as caughtApiNotEnabledException when
            hasReason "accessNotConfigured" caughtApiNotEnabledException
            ->
            return!
                MyDogsbodyException(
                    action,
                    $"{apiNotEnabledPrefix} {googleMessage caughtApiNotEnabledException}",
                    caughtApiNotEnabledException
                )
        | :? Google.GoogleApiException as caughtUsageLimitException when
            caughtUsageLimitException.HttpStatusCode = HttpStatusCode.TooManyRequests
            || (caughtUsageLimitException.HttpStatusCode = HttpStatusCode.Forbidden && isUsageLimit caughtUsageLimitException)
            ->
            return!
                MyDogsbodyException(action, "Google is rate-limiting this account; try again shortly.", caughtUsageLimitException)
        // Google's answer once the whole calendar (not merely one event on it) has been deleted -
        // distinct from an event-level 404, and ahead of the 401/403 clause below, which a
        // calendar deleted out from under a still-valid grant would otherwise be misread as.
        | :? Google.GoogleApiException as caughtCalendarGoneException when
            caughtCalendarGoneException.HttpStatusCode = HttpStatusCode.Gone
            ->
            return! MyDogsbodyException(action, calendarGoneMessage, caughtCalendarGoneException)
        | :? Google.GoogleApiException as caughtUnauthorizedException when
            caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Unauthorized
            || caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Forbidden
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtUnauthorizedException
                )
        | :? TokenResponseException as caughtInvalidGrantException when
            not (isNull caughtInvalidGrantException.Error) && caughtInvalidGrantException.Error.Error = "invalid_grant"
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtInvalidGrantException
                )
        | caughtException ->
            return! MyDogsbodyException(action, $"{unreachablePrefix} {caughtException.Message}", caughtException)
    }

/// The composition root's entry point - the default `HttpClientFactory`.
let listEvents
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    (calendarId: CalendarId)
    (dateRange: CalendarDateRange)
    : Result<CalendarEvent list, MyDogsbodyException> =
    listEventsVia None handleError credential calendarId dateRange

/// The seam `createEvent` closes over.
let createEventVia
    (httpClientFactory: IHttpClientFactory option)
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    (calendarId: CalendarId)
    (syncKey: InvoiceSyncKey)
    (allDayEvent: AllDayEvent)
    : Result<CalendarEventId, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.createEvent

    handleError {
        try
            let initializer =
                BaseClientService.Initializer(HttpClientInitializer = credential, ApplicationName = "MyDogsbody")

            httpClientFactory |> Option.iter (fun factory -> initializer.HttpClientFactory <- factory)

            use service = new CalendarService(initializer)

            let googleEvent = buildAllDayGoogleEvent allDayEvent

            // No reminder (Q2.2) - an explicit empty override list, not merely leaving Reminders
            // unset, which would instead inherit the calendar's own defaults.
            googleEvent.Reminders <- Data.Event.RemindersData(UseDefault = Nullable false, Overrides = ResizeArray())

            let privateExtendedProperties = System.Collections.Generic.Dictionary<string, string>()
            privateExtendedProperties.[InvoiceSyncKey.PropertyName] <- InvoiceSyncKey.value syncKey
            googleEvent.ExtendedProperties <- Data.Event.ExtendedPropertiesData(Private__ = privateExtendedProperties)

            let insertedEvent = service.Events.Insert(googleEvent, CalendarId.value calendarId).Execute()

            let! insertedEventId =
                CalendarEventId.create insertedEvent.Id
                |> Result.mapError (fun reason ->
                    MyDogsbodyException(action, $"Google returned an unusable calendar event id: {reason}", Exception reason))

            return insertedEventId
        with
        | :? Google.GoogleApiException as caughtApiNotEnabledException when
            hasReason "accessNotConfigured" caughtApiNotEnabledException
            ->
            return!
                MyDogsbodyException(
                    action,
                    $"{apiNotEnabledPrefix} {googleMessage caughtApiNotEnabledException}",
                    caughtApiNotEnabledException
                )
        | :? Google.GoogleApiException as caughtUsageLimitException when
            caughtUsageLimitException.HttpStatusCode = HttpStatusCode.TooManyRequests
            || (caughtUsageLimitException.HttpStatusCode = HttpStatusCode.Forbidden && isUsageLimit caughtUsageLimitException)
            ->
            return!
                MyDogsbodyException(action, "Google is rate-limiting this account; try again shortly.", caughtUsageLimitException)
        // Google rejected the event itself - an invalid title, date or property value. Its own
        // sentence is appended the same way apiNotEnabledPrefix's is, so the row that failed says
        // why rather than only that it did.
        | :? Google.GoogleApiException as caughtRejectionException when
            caughtRejectionException.HttpStatusCode = HttpStatusCode.BadRequest
            ->
            return!
                MyDogsbodyException(
                    action,
                    $"{eventRejectedPrefix} {googleMessage caughtRejectionException}",
                    caughtRejectionException
                )
        | :? Google.GoogleApiException as caughtUnauthorizedException when
            caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Unauthorized
            || caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Forbidden
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtUnauthorizedException
                )
        | :? TokenResponseException as caughtInvalidGrantException when
            not (isNull caughtInvalidGrantException.Error) && caughtInvalidGrantException.Error.Error = "invalid_grant"
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtInvalidGrantException
                )
        | caughtException ->
            return! MyDogsbodyException(action, $"{unreachablePrefix} {caughtException.Message}", caughtException)
    }

/// The composition root's entry point - the default `HttpClientFactory`.
let createEvent
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    (calendarId: CalendarId)
    (syncKey: InvoiceSyncKey)
    (allDayEvent: AllDayEvent)
    : Result<CalendarEventId, MyDogsbodyException> =
    createEventVia None handleError credential calendarId syncKey allDayEvent

/// The seam `updateEvent` closes over.
let updateEventVia
    (httpClientFactory: IHttpClientFactory option)
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    (calendarId: CalendarId)
    (eventId: CalendarEventId)
    (allDayEvent: AllDayEvent)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.updateEvent

    handleError {
        try
            let initializer =
                BaseClientService.Initializer(HttpClientInitializer = credential, ApplicationName = "MyDogsbody")

            httpClientFactory |> Option.iter (fun factory -> initializer.HttpClientFactory <- factory)

            use service = new CalendarService(initializer)

            let googleEvent = buildAllDayGoogleEvent allDayEvent

            // PATCH, not Update (PUT): events.update replaces the whole event resource, so any
            // field the request body does not set - extendedProperties (the InvoiceSyncKey
            // createEvent stamped on this event) and reminders among them - is CLEARED
            // server-side, not left alone. buildAllDayGoogleEvent only ever sets Summary,
            // Description, Start and End (Q2.14's "title and date"), so an Update here would
            // silently wipe the sync key on the event's very first update: the next sync would
            // read the event back as keyless (an orphan) and the invoice as unsynced (a fresh,
            // duplicate CreateEvent) - precisely the duplicate requirements.md says the extended
            // property exists to prevent ("the extended property was chosen so a rename would not
            // cause a duplicate"). Patch sends the same fields but merges rather than replaces, so
            // extendedProperties and reminders survive untouched (PR #23 review round 3).
            service.Events.Patch(googleEvent, CalendarId.value calendarId, CalendarEventId.value eventId).Execute()
            |> ignore

            return ()
        with
        | :? Google.GoogleApiException as caughtApiNotEnabledException when
            hasReason "accessNotConfigured" caughtApiNotEnabledException
            ->
            return!
                MyDogsbodyException(
                    action,
                    $"{apiNotEnabledPrefix} {googleMessage caughtApiNotEnabledException}",
                    caughtApiNotEnabledException
                )
        | :? Google.GoogleApiException as caughtUsageLimitException when
            caughtUsageLimitException.HttpStatusCode = HttpStatusCode.TooManyRequests
            || (caughtUsageLimitException.HttpStatusCode = HttpStatusCode.Forbidden && isUsageLimit caughtUsageLimitException)
            ->
            return!
                MyDogsbodyException(action, "Google is rate-limiting this account; try again shortly.", caughtUsageLimitException)
        // Deleted by hand, or the whole calendar has since gone - either way the calendar already
        // agrees with the target state, which is why task 4.2 treats this as AlreadyGone rather
        // than a failure. This function only signals it via the stable message; translating it
        // into CalendarError.EventNoLongerExists happens in the composition root (a later phase).
        | :? Google.GoogleApiException as caughtNotFoundException when
            caughtNotFoundException.HttpStatusCode = HttpStatusCode.NotFound
            ->
            return! MyDogsbodyException(action, eventNotFoundMessage, caughtNotFoundException)
        | :? Google.GoogleApiException as caughtRejectionException when
            caughtRejectionException.HttpStatusCode = HttpStatusCode.BadRequest
            ->
            return!
                MyDogsbodyException(
                    action,
                    $"{eventRejectedPrefix} {googleMessage caughtRejectionException}",
                    caughtRejectionException
                )
        | :? Google.GoogleApiException as caughtUnauthorizedException when
            caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Unauthorized
            || caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Forbidden
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtUnauthorizedException
                )
        | :? TokenResponseException as caughtInvalidGrantException when
            not (isNull caughtInvalidGrantException.Error) && caughtInvalidGrantException.Error.Error = "invalid_grant"
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtInvalidGrantException
                )
        | caughtException ->
            return! MyDogsbodyException(action, $"{unreachablePrefix} {caughtException.Message}", caughtException)
    }

/// The composition root's entry point - the default `HttpClientFactory`.
let updateEvent
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    (calendarId: CalendarId)
    (eventId: CalendarEventId)
    (allDayEvent: AllDayEvent)
    : Result<unit, MyDogsbodyException> =
    updateEventVia None handleError credential calendarId eventId allDayEvent

/// The seam `deleteEvent` closes over.
let deleteEventVia
    (httpClientFactory: IHttpClientFactory option)
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    (calendarId: CalendarId)
    (eventId: CalendarEventId)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.deleteEvent

    handleError {
        try
            let initializer =
                BaseClientService.Initializer(HttpClientInitializer = credential, ApplicationName = "MyDogsbody")

            httpClientFactory |> Option.iter (fun factory -> initializer.HttpClientFactory <- factory)

            use service = new CalendarService(initializer)

            service.Events.Delete(CalendarId.value calendarId, CalendarEventId.value eventId).Execute() |> ignore

            return ()
        with
        | :? Google.GoogleApiException as caughtApiNotEnabledException when
            hasReason "accessNotConfigured" caughtApiNotEnabledException
            ->
            return!
                MyDogsbodyException(
                    action,
                    $"{apiNotEnabledPrefix} {googleMessage caughtApiNotEnabledException}",
                    caughtApiNotEnabledException
                )
        | :? Google.GoogleApiException as caughtUsageLimitException when
            caughtUsageLimitException.HttpStatusCode = HttpStatusCode.TooManyRequests
            || (caughtUsageLimitException.HttpStatusCode = HttpStatusCode.Forbidden && isUsageLimit caughtUsageLimitException)
            ->
            return!
                MyDogsbodyException(action, "Google is rate-limiting this account; try again shortly.", caughtUsageLimitException)
        // Deleted already, or the whole calendar has since gone - either way the calendar already
        // agrees with the target state (task 4.2's AlreadyGone), not a failure.
        | :? Google.GoogleApiException as caughtNotFoundException when
            caughtNotFoundException.HttpStatusCode = HttpStatusCode.NotFound
            ->
            return! MyDogsbodyException(action, eventNotFoundMessage, caughtNotFoundException)
        | :? Google.GoogleApiException as caughtUnauthorizedException when
            caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Unauthorized
            || caughtUnauthorizedException.HttpStatusCode = HttpStatusCode.Forbidden
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtUnauthorizedException
                )
        | :? TokenResponseException as caughtInvalidGrantException when
            not (isNull caughtInvalidGrantException.Error) && caughtInvalidGrantException.Error.Error = "invalid_grant"
            ->
            return!
                MyDogsbodyException(
                    action,
                    "The stored Google credential is no longer authorised.",
                    caughtInvalidGrantException
                )
        | caughtException ->
            return! MyDogsbodyException(action, $"{unreachablePrefix} {caughtException.Message}", caughtException)
    }

/// The composition root's entry point - the default `HttpClientFactory`.
let deleteEvent
    (handleError: HandleErrorBuilder)
    (credential: IConfigurableHttpClientInitializer)
    (calendarId: CalendarId)
    (eventId: CalendarEventId)
    : Result<unit, MyDogsbodyException> =
    deleteEventVia None handleError credential calendarId eventId
