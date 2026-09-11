module MyDogsbody.Tests.Contracts.ListCalendarsDependencyContractTests

open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google

/// `ListCalendars` is the one dependency function type in this change whose real implementation
/// is a network service (friction #2). Per design.md's arrangement, "real" here means the real
/// adapter (`GoogleCalendarClient.listCalendarsVia`) driven by a stubbed `HttpMessageHandler`
/// rather than the network - exercising the adapter's own request-building, paging and
/// response-parsing, which is the part that can actually be wrong. Live verification against
/// Google itself is recorded as manual coverage in outcome.md, not silently skipped.

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

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private jsonResponse (statusCode: HttpStatusCode) (body: string) =
    let response = new HttpResponseMessage(statusCode)
    response.Content <- new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    response

let private calendarListPage (items: string) =
    $"""{{ "kind": "calendar#calendarList", "items": [ {items} ] }}"""

let private calendarEntry id summary isPrimary =
    $"""{{ "kind": "calendar#calendarListEntry", "id": "{id}", "summary": "{summary}", "primary": {isPrimary} }}"""

let private accountId = GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail

// ---------- the real adapter, driven by a stubbed HttpMessageHandler ----------

let private withRealListCalendarsAs
    (credential: Google.Apis.Http.IConfigurableHttpClientInitializer)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    (test: ListCalendars -> unit)
    =
    let handleError = HandleErrorBuilder(fun _ -> ())
    let handler = new RespondingHandler(respond)
    let factory = StubHttpClientFactory handler :> Google.Apis.Http.IHttpClientFactory

    let listCalendars: ListCalendars =
        fun accountId ->
            GoogleCalendarClient.listCalendarsVia (Some factory) handleError credential ()
            |> Result.mapError (fun ex ->
                if ex.Message = "The stored Google credential is no longer authorised." then NotAuthorised accountId
                elif ex.Message = "Google is rate-limiting this account; try again shortly." then
                    CalendarRateLimited ex.Message
                else
                    CalendarUnreachable ex.Message)

    test listCalendars

let private withRealListCalendars (respond: HttpRequestMessage -> HttpResponseMessage) (test: ListCalendars -> unit) =
    withRealListCalendarsAs (NoopInitializer() :> Google.Apis.Http.IConfigurableHttpClientInitializer) respond test

// ---------- the in-memory fake ----------

let private withFakeListCalendars (calendars: AvailableCalendar list) (test: ListCalendars -> unit) =
    test (fun _ -> Ok calendars)

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"{label} expected Ok, but got Error: {error}"

// ---------- the shared behaviour ----------

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter returns every calendar in a single page`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.OK (calendarListPage (calendarEntry "primary" "My Calendar" "true"))

    withRealListCalendars respond (fun listCalendars ->
        let actual = listCalendars accountId |> okOrFail "listCalendars"
        let calendar = Assert.Single actual
        Assert.Equal("primary", CalendarId.value calendar.Id)
        Assert.True calendar.IsPrimary
    )

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 401 to NotAuthorised carrying the account id`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.Unauthorized
            """{ "error": { "code": 401, "message": "Invalid Credentials", "errors": [] } }"""

    withRealListCalendars respond (fun listCalendars ->
        Assert.Equal(Error(NotAuthorised accountId), listCalendars accountId)
    )

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 429 to CalendarRateLimited, distinct from NotAuthorised`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.TooManyRequests
            """{ "error": { "code": 429, "message": "Rate Limit Exceeded", "errors": [] } }"""

    withRealListCalendars respond (fun listCalendars ->
        match listCalendars accountId with
        | Error(CalendarRateLimited _) -> ()
        | other -> Assert.Fail($"Expected CalendarRateLimited, got {other}")
    )

[<Fact; Trait("Level", "Contract")>]
let ``the in-memory fake returns exactly the calendars it was given`` () =
    let calendars: AvailableCalendar list =
        [ { Id = CalendarId.create "cal-1" |> valueOrFail; Name = CalendarName.create "Invoices" |> valueOrFail; IsPrimary = true } ]

    withFakeListCalendars calendars (fun listCalendars ->
        Assert.Equal(Ok calendars, listCalendars accountId)
    )

[<Theory; Trait("Level", "Contract")>]
[<InlineData("userRateLimitExceeded")>]
[<InlineData("rateLimitExceeded")>]
[<InlineData("quotaExceeded")>]
[<InlineData("dailyLimitExceeded")>]
let ``the real adapter maps a usage-limit 403 to CalendarRateLimited, not NotAuthorised`` (reason: string) =
    // Google documents its usage limits as 403s as well as 429s; only error.errors[].reason tells
    // them apart from a permission failure, and they need opposite responses from the user.
    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.Forbidden
            $"""{{ "error": {{ "code": 403, "message": "Rate Limit Exceeded", "errors": [ {{ "domain": "usageLimits", "reason": "{reason}", "message": "Rate Limit Exceeded" }} ] }} }}"""

    withRealListCalendars respond (fun listCalendars ->
        Assert.Equal(
            Error(CalendarRateLimited "Google is rate-limiting this account; try again shortly."),
            listCalendars accountId
        )
    )

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

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a refresh token Google has expired or revoked to NotAuthorised carrying the account id`` () =
    // requirements.md: "WHEN a stored token has expired or been revoked THE SYSTEM SHALL report the
    // account as needing re-authorisation" - and with Google, that refusal comes from the token
    // endpoint during a refresh, not as a 401 from the Calendar API itself.
    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.Unauthorized
            """{ "error": { "code": 401, "message": "Invalid Credentials", "errors": [] } }"""

    withRealListCalendarsAs (credentialGoogleWillNotRefresh (System.DateTime.UtcNow.AddHours -2.0)) respond (fun listCalendars ->
        Assert.Equal(Error(NotAuthorised accountId), listCalendars accountId)
    )
