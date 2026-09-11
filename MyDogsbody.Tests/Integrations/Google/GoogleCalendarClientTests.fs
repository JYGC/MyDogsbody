module MyDogsbody.Tests.Integrations.Google.GoogleCalendarClientTests

open System.Net
open System.Net.Http
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

let private listCalendars (respond: HttpRequestMessage -> HttpResponseMessage) =
    let handler = new RespondingHandler(respond)
    let factory = StubHttpClientFactory handler :> Google.Apis.Http.IHttpClientFactory
    let credential = NoopInitializer() :> Google.Apis.Http.IConfigurableHttpClientInitializer
    let handleError = HandleErrorBuilder(fun _ -> ())

    GoogleCalendarClient.listCalendarsVia (Some factory) handleError credential ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

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
    Assert.Contains(actual, fun c -> CalendarId.value c.Id = "primary" && c.IsPrimary)
    Assert.Contains(actual, fun c -> CalendarId.value c.Id = "cal-2" && not c.IsPrimary)

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

    let ids = actual |> List.map (fun c -> CalendarId.value c.Id) |> List.sort
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
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars, ex.ActionName)
        Assert.Equal("The stored Google credential is no longer authorised.", ex.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars maps a 403 to the same not-authorised message as a 401`` () =
    let respond (_: HttpRequestMessage) = jsonResponse HttpStatusCode.Forbidden (errorBody 403 "Permission denied")

    match listCalendars respond with
    | Error ex -> Assert.Equal("The stored Google credential is no longer authorised.", ex.Message)
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
    | Error ex ->
        Assert.StartsWith("The Google Calendar API is not enabled for this project.", ex.Message)
        Assert.NotEqual<string>("The stored Google credential is no longer authorised.", ex.Message)
        // Google's own sentence survives, because it names the project and the URL that fixes it.
        Assert.Contains("000000000000", ex.Message)
        Assert.Contains("console.developers.google.com", ex.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars maps a 429 to a distinct rate-limit message, not the not-authorised one`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.TooManyRequests (errorBody 429 "Rate Limit Exceeded")

    match listCalendars respond with
    | Error ex -> Assert.Equal("Google is rate-limiting this account; try again shortly.", ex.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``listCalendars maps a 500 to an unreachable message`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.InternalServerError (errorBody 500 "Backend Error")

    match listCalendars respond with
    | Error ex ->
        // The opening is stable (the mapper matches on it); Google's own text is appended, so a
        // user is not left with a bare "could not reach" and nothing to act on.
        Assert.StartsWith("Could not reach Google Calendar.", ex.Message)
        Assert.Contains("Backend Error", ex.Message)
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
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars, ex.ActionName)
        Assert.Equal("Google is rate-limiting this account; try again shortly.", ex.Message)
        let inner = Assert.IsType<Google.GoogleApiException>(ex.InnerException)
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
    | Error ex -> Assert.Equal("The stored Google credential is no longer authorised.", ex.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
