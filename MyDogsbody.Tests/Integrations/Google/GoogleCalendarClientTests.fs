module MyDogsbody.Tests.Integrations.Google.GoogleCalendarClientTests

open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google

/// The real adapter, driven by a stubbed `HttpMessageHandler` rather than a network connection -
/// design.md's own description of how this dependency's contract level is satisfied without
/// reaching Google (friction #2). No test in this file needs network access or credentials.

type private RespondingHandler(respond: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        Task.FromResult(respond request)

type private StubHttpClientFactory(handler: HttpMessageHandler) =
    inherit Google.Apis.Http.HttpClientFactory()
    override _.CreateHandler(_args: Google.Apis.Http.CreateHttpClientArgs) : HttpMessageHandler = handler

type private NoopInitializer() =
    interface Google.Apis.Http.IConfigurableHttpClientInitializer with
        member _.Initialize(_httpClient) = ()

let private jsonResponse (statusCode: HttpStatusCode) (body: string) =
    let response = new HttpResponseMessage(statusCode)
    response.Content <- new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    response

let private calendarListPage (items: string) (nextPageToken: string option) =
    let tokenField =
        match nextPageToken with
        | Some token -> $""""nextPageToken": "{token}","""
        | None -> ""

    $"""{{ "kind": "calendar#calendarList", {tokenField} "items": [ {items} ] }}"""

let private calendarEntry id summary isPrimary =
    $"""{{ "kind": "calendar#calendarListEntry", "id": "{id}", "summary": "{summary}", "primary": {isPrimary} }}"""

let private errorBody (code: int) (message: string) =
    $"""{{ "error": {{ "code": {code}, "message": "{message}", "errors": [ {{ "domain": "global", "reason": "error", "message": "{message}" }} ] }} }}"""

let private errorBodyWithReason (code: int) (reason: string) (message: string) =
    $"""{{ "error": {{ "code": {code}, "message": "{message}", "errors": [ {{ "domain": "usageLimits", "reason": "{reason}", "message": "{message}" }} ] }} }}"""

let private listCalendarsAs
    (credential: Google.Apis.Http.IConfigurableHttpClientInitializer)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    =
    let handler = new RespondingHandler(respond)
    let factory = StubHttpClientFactory handler :> Google.Apis.Http.IHttpClientFactory
    let handleError = HandleErrorBuilder(fun _ -> ())

    GoogleCalendarClient.listCalendarsVia (Some factory) handleError credential ()

let private listCalendars (respond: HttpRequestMessage -> HttpResponseMessage) =
    listCalendarsAs (NoopInitializer() :> Google.Apis.Http.IConfigurableHttpClientInitializer) respond

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (caughtException: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {caughtException.Message}"

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars returns every calendar in a single page`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.OK
            (calendarListPage
                (calendarEntry "primary" "My Calendar" "true" + "," + calendarEntry "cal-2" "Team Calendar" "false")
                None)

    let actual = listCalendars respond |> okOrFail "listCalendars"

    Assert.Equal(2, List.length actual)
    Assert.Contains(actual, fun calendar -> CalendarId.value calendar.Id = "primary" && calendar.IsPrimary)
    Assert.Contains(actual, fun calendar -> CalendarId.value calendar.Id = "cal-2" && not calendar.IsPrimary)

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars follows nextPageToken and returns items from every page`` () =
    let respond (request: HttpRequestMessage) =
        let query = request.RequestUri.Query

        if query.Contains "pageToken=page-2" then
            jsonResponse HttpStatusCode.OK (calendarListPage (calendarEntry "cal-2" "Second Page Calendar" "false") None)
        else
            jsonResponse
                HttpStatusCode.OK
                (calendarListPage (calendarEntry "cal-1" "First Page Calendar" "false") (Some "page-2"))

    let actual = listCalendars respond |> okOrFail "listCalendars"

    let ids = actual |> List.map (fun calendar -> CalendarId.value calendar.Id) |> List.sort
    Assert.Equal<string list>([ "cal-1"; "cal-2" ], ids)

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars returns an empty list when the account has no calendars`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.OK """{ "kind": "calendar#calendarList", "items": [] }"""

    let actual = listCalendars respond |> okOrFail "listCalendars"
    Assert.Empty actual

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars maps a 401 to a not-authorised message`` () =
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.Unauthorized (errorBody 401 "Invalid Credentials")

    match listCalendars respond with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars, caughtException.ActionName)
        Assert.Equal("The stored Google credential is no longer authorised.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars maps a 403 to the same not-authorised message as a 401`` () =
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.Forbidden (errorBody 403 "Permission denied")

    match listCalendars respond with
    | Error caughtException -> Assert.Equal("The stored Google credential is no longer authorised.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars tells a project with the Calendar API switched off apart from a credential problem`` () =
    // Both arrive as a bare 403; only `error.errors[].reason` separates them, and they need
    // opposite responses - "enable the API for this project" versus "re-authorise this account".
    // Reported from real use, where every attempt read as the latter and no amount of
    // re-authorising could have helped.
    let googleSentence =
        "Google Calendar API has not been used in project 000000000000 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/calendar-json.googleapis.com/overview?project=000000000000 then retry."

    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.Forbidden (errorBodyWithReason 403 "accessNotConfigured" googleSentence)

    match listCalendars respond with
    | Error caughtException ->
        Assert.StartsWith("The Google Calendar API is not enabled for this project.", caughtException.Message)
        Assert.NotEqual<string>("The stored Google credential is no longer authorised.", caughtException.Message)
        // Google's own sentence survives, because it names the project and the URL that fixes it.
        Assert.Contains("000000000000", caughtException.Message)
        Assert.Contains("console.developers.google.com", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars maps a 429 to a distinct rate-limit message, not the not-authorised one`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.TooManyRequests (errorBody 429 "Rate Limit Exceeded")

    match listCalendars respond with
    | Error caughtException -> Assert.Equal("Google is rate-limiting this account; try again shortly.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars maps a 500 to an unreachable message`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.InternalServerError (errorBody 500 "Backend Error")

    match listCalendars respond with
    | Error caughtException ->
        // The opening is stable (the mapper matches on it); Google's own text is appended, so a
        // user is not left with a bare "could not reach" and nothing to act on.
        Assert.StartsWith("Could not reach Google Calendar.", caughtException.Message)
        Assert.Contains("Backend Error", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Theory; Trait("Level", "Integration")>]
[<InlineData("userRateLimitExceeded", "User Rate Limit Exceeded")>]
[<InlineData("rateLimitExceeded", "Rate Limit Exceeded")>]
[<InlineData("quotaExceeded", "Calendar usage limits exceeded.")>]
[<InlineData("dailyLimitExceeded", "Daily Limit Exceeded")>]
let ``listCalendars maps a usage-limit 403 to the rate-limit message, not the not-authorised one``
    (reason: string, googleMessage: string)
    =
    // Google's Calendar API error guide documents its usage limits as 403s, not only as 429s - these
    // bodies are the guide's own shape, domain "usageLimits". Its remedy is to back off and retry;
    // re-authorising fixes none of them. requirements.md: "WHEN Google returns a rate-limit or
    // transient error THE SYSTEM SHALL report it distinctly from a permission failure".
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.Forbidden (errorBodyWithReason 403 reason googleMessage)

    match listCalendars respond with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars, caughtException.ActionName)
        Assert.Equal("Google is rate-limiting this account; try again shortly.", caughtException.Message)
        let inner = Assert.IsType<Google.GoogleApiException>(caughtException.InnerException)
        Assert.Equal(HttpStatusCode.Forbidden, inner.HttpStatusCode)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars still maps a 403 for too-narrow scopes to the not-authorised message`` () =
    // The 403 that re-consenting DOES fix - seen in task 10.4's real use, a token predating the
    // calendar scope. The usage-limit reasons above must not swallow it.
    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.Forbidden
            """{ "error": { "code": 403, "message": "Insufficient Permission", "errors": [ { "domain": "global", "reason": "insufficientPermissions", "message": "Insufficient Permission" } ] } }"""

    match listCalendars respond with
    | Error caughtException -> Assert.Equal("The stored Google credential is no longer authorised.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

/// Somewhere for the library to delete a token from: Google.Apis.Auth deletes the stored token
/// itself when the token endpoint refuses a refresh, so the flow needs a data store to call.
type private InMemoryDataStore() =
    let entries = System.Collections.Concurrent.ConcurrentDictionary<string, obj>()

    interface Google.Apis.Util.Store.IDataStore with
        member _.StoreAsync<'T>(key: string, value: 'T) : Task =
            entries.[$"{typeof<'T>.FullName}:{key}"] <- box value
            Task.CompletedTask

        member _.GetAsync<'T>(key: string) : Task<'T> =
            match entries.TryGetValue $"{typeof<'T>.FullName}:{key}" with
            | true, value -> Task.FromResult(unbox<'T> value)
            | _ -> Task.FromResult(Unchecked.defaultof<'T>)

        member _.DeleteAsync<'T>(key: string) : Task =
            entries.TryRemove $"{typeof<'T>.FullName}:{key}" |> ignore
            Task.CompletedTask

        member _.ClearAsync() : Task =
            entries.Clear()
            Task.CompletedTask

/// A real `UserCredential` whose refresh token Google's token endpoint refuses - `invalid_grant`,
/// "Token has been expired or revoked.", which is Google's answer once the user revokes the app at
/// Google, or once a Testing-mode OAuth client's seven-day refresh-token lifetime runs out.
let private credentialGoogleWillNotRefresh (accessTokenIssuedUtc: System.DateTime) =
    let tokenEndpoint (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.BadRequest
            """{ "error": "invalid_grant", "error_description": "Token has been expired or revoked." }"""

    let flow =
        new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(
            Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer(
                ClientSecrets = Google.Apis.Auth.OAuth2.ClientSecrets(ClientId = "client-id", ClientSecret = "client-secret"),
                Scopes = GoogleAuthorization.scopes,
                DataStore = InMemoryDataStore(),
                HttpClientFactory = StubHttpClientFactory(new RespondingHandler(tokenEndpoint))
            )
        )

    let token =
        Google.Apis.Auth.OAuth2.Responses.TokenResponse(
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            ExpiresInSeconds = System.Nullable 3599L,
            IssuedUtc = accessTokenIssuedUtc
        )

    Google.Apis.Auth.OAuth2.UserCredential(flow, "account-1", token)
    :> Google.Apis.Http.IConfigurableHttpClientInitializer

[<Theory; Trait("Level", "Integration")>]
[<InlineData(2.0)>]
[<InlineData(0.0)>]
let ``listCalendars maps a refresh token Google has expired or revoked to the not-authorised message``
    (accessTokenAgeInHours: float)
    =
    // 2 hours: the access token has lapsed, so the credential refreshes before the call is sent.
    // 0 hours: the access token still looks live, Google answers 401, and the credential refreshes
    // in response. Either way it is Google's token endpoint that refuses, not the Calendar API, so
    // the failure is a TokenResponseException rather than a GoogleApiException - and it used to
    // fall to the catch-all as "Could not reach Google Calendar.": a revoked grant reported as a
    // network problem, where requirements.md asks for "needing re-authorisation".
    let credential =
        credentialGoogleWillNotRefresh (System.DateTime.UtcNow.AddHours(-accessTokenAgeInHours))

    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.Unauthorized (errorBody 401 "Invalid Credentials")

    match listCalendarsAs credential respond with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars, caughtException.ActionName)
        Assert.Equal("The stored Google credential is no longer authorised.", caughtException.Message)
        let inner = Assert.IsType<Google.Apis.Auth.OAuth2.Responses.TokenResponseException>(caughtException.InnerException)
        Assert.Equal("invalid_grant", inner.Error.Error)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

let private calendarEntryWithRole id summary isPrimary accessRole =
    $"""{{ "kind": "calendar#calendarListEntry", "id": "{id}", "summary": "{summary}", "primary": {isPrimary}, "accessRole": "{accessRole}" }}"""

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars offers only calendars the account can add events to, on every page`` () =
    // Google's calendar list also holds calendars the account can only read - "Holidays in
    // Australia", "Birthdays", anything subscribed to. Offering one in the picker lets it be chosen,
    // and the account then shows Ready with a default calendar no invoice event can ever be written
    // to. The stub answers the way Google does: read-only entries are left out only when the
    // request asks for writer access, which each page's request has to do for itself.
    let owned = calendarEntryWithRole "primary" "My Calendar" "true" "owner"
    let holidays = calendarEntryWithRole "holidays@group.v.calendar.google.com" "Holidays in Australia" "false" "reader"
    let shared = calendarEntryWithRole "team@group.calendar.google.com" "Team Calendar" "false" "writer"
    let birthdays = calendarEntryWithRole "addressbook#contacts@group.v.calendar.google.com" "Birthdays" "false" "reader"

    let respond (request: HttpRequestMessage) =
        let query = request.RequestUri.Query
        let writableOnly = query.Contains "minAccessRole=writer"

        if query.Contains "pageToken=page-2" then
            let items = if writableOnly then shared else shared + "," + birthdays
            jsonResponse HttpStatusCode.OK (calendarListPage items None)
        else
            let items = if writableOnly then owned else owned + "," + holidays
            jsonResponse HttpStatusCode.OK (calendarListPage items (Some "page-2"))

    let actual = listCalendars respond |> okOrFail "listCalendars"

    let ids = actual |> List.map (fun calendar -> CalendarId.value calendar.Id) |> List.sort
    Assert.Equal<string list>([ "primary"; "team@group.calendar.google.com" ], ids)

let private calendarEntryRenamed id summary summaryOverride isPrimary =
    $"""{{ "kind": "calendar#calendarListEntry", "id": "{id}", "summary": "{summary}", "summaryOverride": "{summaryOverride}", "primary": {isPrimary}, "accessRole": "writer" }}"""

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars names each calendar the way the account's own calendar list does`` () =
    // `summary` is the title the calendar's owner gave it; `summaryOverride` is the name this account
    // gave it in its own list, which is what Google Calendar shows the user. Two people's shared
    // calendars, both titled "Invoices" and renamed by this account to tell them apart, reached the
    // picker as two indistinguishable "Invoices" - a choice the user could get wrong with nothing to
    // say so. A calendar with no override keeps its title.
    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.OK
            (calendarListPage
                (calendarEntryRenamed "alice@group.calendar.google.com" "Invoices" "Invoices - Alice" "false"
                 + ","
                 + calendarEntryRenamed "bob@group.calendar.google.com" "Invoices" "Invoices - Bob" "false"
                 + ","
                 + calendarEntryWithRole "primary" "person@example.com" "true" "owner")
                None)

    let actual =
        listCalendars respond
        |> okOrFail "listCalendars"
        |> List.map (fun calendar -> CalendarId.value calendar.Id, CalendarName.value calendar.Name, calendar.IsPrimary)

    Assert.Equal<(string * string * bool) list>(
        [
            "alice@group.calendar.google.com", "Invoices - Alice", false
            "bob@group.calendar.google.com", "Invoices - Bob", false
            "primary", "person@example.com", true
        ],
        actual
    )

// -------------------------------------------------------------------------------------------
// Change #7 (invoice-calendar-sync) - listEvents, createEvent, updateEvent, deleteEvent.
// Same stubbed-HttpMessageHandler approach as listCalendars above; no test here needs network
// access or credentials, and none makes a real Google call.
// -------------------------------------------------------------------------------------------

let private requireOk (label: string) (result: Result<'a, string>) : 'a =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"{label}: {reason}"

let private testCalendarId = CalendarId.create "cal-1" |> requireOk "test calendar id"

let private testDateRange =
    CalendarDateRange.create (System.DateTime(2026, 9, 1)) (System.DateTime(2026, 9, 30))
    |> requireOk "test date range"

/// The same ASCII Unit Separator `InvoiceSyncKey.fs` documents its own `fieldSeparator` as -
/// not a reach into that private value, just the same well-known encoding used to build a
/// syntactically valid key for `InvoiceSyncKey.parse` to accept.
let private fieldSeparatorChar = char 0x1F

let private testSyncKey =
    InvoiceSyncKey.parse $"supplier-1{fieldSeparatorChar}INV-100"
    |> requireOk "test invoice sync key"

let private testEventId = CalendarEventId.create "event-1" |> requireOk "test calendar event id"

/// JSON forbids an unescaped control character inside a string, so a hand-written response body
/// embedding `testSyncKey`'s raw separator character has to escape it the way a real JSON writer
/// would, or the stub response would not be valid JSON for the client to parse.
let private jsonEscapedSyncKeyValue (syncKey: InvoiceSyncKey) =
    (InvoiceSyncKey.value syncKey).Replace(string fieldSeparatorChar, "\\u001f")

/// `BaseClientService.Initializer.GZipEnabled` defaults to `true`, so every write request this
/// adapter sends arrives here already GZip-compressed - reading it as a plain UTF-8 string would
/// see the GZip magic bytes rather than JSON. A captured-body assertion has to undo that first.
let private readGZipCompressedRequestBodyAsText (request: HttpRequestMessage) : string =
    let compressedBytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    use compressedStream = new System.IO.MemoryStream(compressedBytes)
    use decompressingStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress)
    use decompressedTextReader = new System.IO.StreamReader(decompressingStream, System.Text.Encoding.UTF8)
    decompressedTextReader.ReadToEnd()

let private eventsPage (items: string) (nextPageToken: string option) =
    let tokenField =
        match nextPageToken with
        | Some token -> $""""nextPageToken": "{token}","""
        | None -> ""

    $"""{{ "kind": "calendar#events", {tokenField} "items": [ {items} ] }}"""

let private eventEntryWithoutSyncKey id summary description startDate endDate =
    $"""{{ "kind": "calendar#event", "id": "{id}", "summary": "{summary}", "description": "{description}", "start": {{ "date": "{startDate}" }}, "end": {{ "date": "{endDate}" }} }}"""

let private eventEntryWithSyncKey id summary description startDate endDate (escapedSyncKeyValue: string) =
    $"""{{ "kind": "calendar#event", "id": "{id}", "summary": "{summary}", "description": "{description}", "start": {{ "date": "{startDate}" }}, "end": {{ "date": "{endDate}" }}, "extendedProperties": {{ "private": {{ "{InvoiceSyncKey.PropertyName}": "{escapedSyncKeyValue}" }} }} }}"""

let private listEventsAs
    (credential: Google.Apis.Http.IConfigurableHttpClientInitializer)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    =
    let handler = new RespondingHandler(respond)
    let factory = StubHttpClientFactory handler :> Google.Apis.Http.IHttpClientFactory
    let handleError = HandleErrorBuilder(fun _ -> ())

    GoogleCalendarClient.listEventsVia (Some factory) handleError credential testCalendarId testDateRange

let private listEvents (respond: HttpRequestMessage -> HttpResponseMessage) =
    listEventsAs (NoopInitializer() :> Google.Apis.Http.IConfigurableHttpClientInitializer) respond

let private createEventAs
    (credential: Google.Apis.Http.IConfigurableHttpClientInitializer)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    (allDayEvent: AllDayEvent)
    =
    let handler = new RespondingHandler(respond)
    let factory = StubHttpClientFactory handler :> Google.Apis.Http.IHttpClientFactory
    let handleError = HandleErrorBuilder(fun _ -> ())

    GoogleCalendarClient.createEventVia (Some factory) handleError credential testCalendarId testSyncKey allDayEvent

let private createEvent (respond: HttpRequestMessage -> HttpResponseMessage) (allDayEvent: AllDayEvent) =
    createEventAs (NoopInitializer() :> Google.Apis.Http.IConfigurableHttpClientInitializer) respond allDayEvent

let private updateEventAs
    (credential: Google.Apis.Http.IConfigurableHttpClientInitializer)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    (eventId: CalendarEventId)
    (allDayEvent: AllDayEvent)
    =
    let handler = new RespondingHandler(respond)
    let factory = StubHttpClientFactory handler :> Google.Apis.Http.IHttpClientFactory
    let handleError = HandleErrorBuilder(fun _ -> ())

    GoogleCalendarClient.updateEventVia (Some factory) handleError credential testCalendarId eventId allDayEvent

let private updateEvent (respond: HttpRequestMessage -> HttpResponseMessage) (eventId: CalendarEventId) (allDayEvent: AllDayEvent) =
    updateEventAs (NoopInitializer() :> Google.Apis.Http.IConfigurableHttpClientInitializer) respond eventId allDayEvent

let private deleteEventAs
    (credential: Google.Apis.Http.IConfigurableHttpClientInitializer)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    (eventId: CalendarEventId)
    =
    let handler = new RespondingHandler(respond)
    let factory = StubHttpClientFactory handler :> Google.Apis.Http.IHttpClientFactory
    let handleError = HandleErrorBuilder(fun _ -> ())

    GoogleCalendarClient.deleteEventVia (Some factory) handleError credential testCalendarId eventId

let private deleteEvent (respond: HttpRequestMessage -> HttpResponseMessage) (eventId: CalendarEventId) =
    deleteEventAs (NoopInitializer() :> Google.Apis.Http.IConfigurableHttpClientInitializer) respond eventId

[<Fact; Trait("Level", "Integration")>]
let ``listEvents returns Some for an event carrying the sync property and None for one added by hand`` () =
    // requirements.md / task 5.1: an event with the extended property present comes back with
    // SyncKey = Some; an event without it - added by hand, on the account's own calendar - comes
    // back with SyncKey = None rather than being excluded, so the diff (never this function) is
    // what decides it is an orphan.
    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.OK
            (eventsPage
                (eventEntryWithSyncKey
                    "event-1"
                    "Acme Corp - INV-100"
                    "Due 2026-09-20"
                    "2026-09-20"
                    "2026-09-21"
                    (jsonEscapedSyncKeyValue testSyncKey)
                 + ","
                 + eventEntryWithoutSyncKey "event-2" "Team lunch" "" "2026-09-21" "2026-09-22")
                None)

    let actual = listEvents respond |> okOrFail "listEvents"

    let syncedEvent = actual |> List.find (fun calendarEvent -> CalendarEventId.value calendarEvent.Id = "event-1")
    let handAddedEvent = actual |> List.find (fun calendarEvent -> CalendarEventId.value calendarEvent.Id = "event-2")

    Assert.Equal(Some testSyncKey, syncedEvent.SyncKey)
    Assert.Equal(System.DateTime(2026, 9, 20), syncedEvent.Event.Date)
    Assert.Equal("Acme Corp - INV-100", syncedEvent.Event.Title)
    Assert.Equal("Due 2026-09-20", syncedEvent.Event.Description)

    Assert.Equal(None, handAddedEvent.SyncKey)
    Assert.Equal(System.DateTime(2026, 9, 21), handAddedEvent.Event.Date)
    Assert.Equal("Team lunch", handAddedEvent.Event.Title)

[<Fact; Trait("Level", "Integration")>]
let ``listEvents follows nextPageToken and returns items from every page`` () =
    let respond (request: HttpRequestMessage) =
        let query = request.RequestUri.Query

        if query.Contains "pageToken=page-2" then
            jsonResponse
                HttpStatusCode.OK
                (eventsPage (eventEntryWithoutSyncKey "event-2" "Second page event" "" "2026-09-22" "2026-09-23") None)
        else
            jsonResponse
                HttpStatusCode.OK
                (eventsPage (eventEntryWithoutSyncKey "event-1" "First page event" "" "2026-09-20" "2026-09-21") (Some "page-2"))

    let actual = listEvents respond |> okOrFail "listEvents"

    let ids = actual |> List.map (fun calendarEvent -> CalendarEventId.value calendarEvent.Id) |> List.sort
    Assert.Equal<string list>([ "event-1"; "event-2" ], ids)

[<Fact; Trait("Level", "Integration")>]
let ``listEvents maps a 403 to the not-authorised message`` () =
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.Forbidden (errorBody 403 "Permission denied")

    match listEvents respond with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listEvents, caughtException.ActionName)
        Assert.Equal("The stored Google credential is no longer authorised.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listEvents maps a 429 to the rate-limit message, not the not-authorised one`` () =
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.TooManyRequests (errorBody 429 "Rate Limit Exceeded")

    match listEvents respond with
    | Error caughtException -> Assert.Equal("Google is rate-limiting this account; try again shortly.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listEvents maps a 410 to the calendar-no-longer-exists message`` () =
    // Google's answer once the whole calendar - not merely one event on it - has been deleted.
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.Gone (errorBody 410 "Resource has been deleted")

    match listEvents respond with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listEvents, caughtException.ActionName)
        Assert.Equal("The calendar no longer exists.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``createEvent sends an all-day start date, not a timed dateTime, and stamps the derived sync key`` () =
    let mutable capturedRequestBody = ""

    let allDayEvent =
        { Date = System.DateTime(2026, 9, 20)
          Title = "Acme Corp - INV-100"
          Description = "Due 2026-09-20" }

    let respond (request: HttpRequestMessage) =
        capturedRequestBody <- readGZipCompressedRequestBodyAsText request

        jsonResponse
            HttpStatusCode.OK
            """{ "kind": "calendar#event", "id": "created-event-1", "start": { "date": "2026-09-20" }, "end": { "date": "2026-09-21" } }"""

    let actual = createEvent respond allDayEvent |> okOrFail "createEvent"

    Assert.Equal("created-event-1", CalendarEventId.value actual)

    let requestBodyDocument = JsonDocument.Parse(capturedRequestBody)
    let requestRoot = requestBodyDocument.RootElement

    let startElement = requestRoot.GetProperty("start")
    Assert.Equal("2026-09-20", startElement.GetProperty("date").GetString())
    Assert.DoesNotContain("dateTime", startElement.EnumerateObject() |> Seq.map (fun property -> property.Name))

    let endElement = requestRoot.GetProperty("end")
    Assert.Equal("2026-09-21", endElement.GetProperty("date").GetString())

    let remindersElement = requestRoot.GetProperty("reminders")
    Assert.False(remindersElement.GetProperty("useDefault").GetBoolean())

    let privateExtendedPropertiesElement = requestRoot.GetProperty("extendedProperties").GetProperty("private")
    Assert.Equal(InvoiceSyncKey.value testSyncKey, privateExtendedPropertiesElement.GetProperty(InvoiceSyncKey.PropertyName).GetString())

[<Fact; Trait("Level", "Integration")>]
let ``createEvent maps a 400 rejection to the event-rejected message`` () =
    let allDayEvent = { Date = System.DateTime(2026, 9, 20); Title = ""; Description = "" }
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.BadRequest (errorBody 400 "Invalid summary value.")

    match createEvent respond allDayEvent with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.createEvent, caughtException.ActionName)
        Assert.StartsWith("Google rejected the calendar event.", caughtException.Message)
        Assert.Contains("Invalid summary value.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``updateEvent succeeds and returns unit`` () =
    let allDayEvent =
        { Date = System.DateTime(2026, 9, 21)
          Title = "Acme Corp - INV-100 (updated)"
          Description = "" }

    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.OK
            """{ "kind": "calendar#event", "id": "event-1", "start": { "date": "2026-09-21" }, "end": { "date": "2026-09-22" } }"""

    let actual = updateEvent respond testEventId allDayEvent |> okOrFail "updateEvent"
    Assert.Equal((), actual)

[<Fact; Trait("Level", "Integration")>]
let ``updateEvent maps a 404 to the event-no-longer-exists message`` () =
    // task 4.2 relies on this being a success (AlreadyGone) rather than a failure once the
    // composition root translates it - here it only has to be a stable, distinguishable message.
    let allDayEvent =
        { Date = System.DateTime(2026, 9, 21)
          Title = "Acme Corp - INV-100"
          Description = "" }

    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.NotFound (errorBody 404 "Not Found")

    match updateEvent respond testEventId allDayEvent with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.updateEvent, caughtException.ActionName)
        Assert.Equal("The calendar event no longer exists.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``updateEvent maps a 403 to the not-authorised message`` () =
    let allDayEvent =
        { Date = System.DateTime(2026, 9, 21)
          Title = "Acme Corp - INV-100"
          Description = "" }

    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.Forbidden (errorBody 403 "Permission denied")

    match updateEvent respond testEventId allDayEvent with
    | Error caughtException -> Assert.Equal("The stored Google credential is no longer authorised.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``deleteEvent succeeds and returns unit`` () =
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.OK "\"\""

    let actual = deleteEvent respond testEventId |> okOrFail "deleteEvent"
    Assert.Equal((), actual)

[<Fact; Trait("Level", "Integration")>]
let ``deleteEvent maps a 404 to the event-no-longer-exists message`` () =
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.NotFound (errorBody 404 "Not Found")

    match deleteEvent respond testEventId with
    | Error caughtException ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.deleteEvent, caughtException.ActionName)
        Assert.Equal("The calendar event no longer exists.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``deleteEvent maps a 403 to the not-authorised message`` () =
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.Forbidden (errorBody 403 "Permission denied")

    match deleteEvent respond testEventId with
    | Error caughtException -> Assert.Equal("The stored Google credential is no longer authorised.", caughtException.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
