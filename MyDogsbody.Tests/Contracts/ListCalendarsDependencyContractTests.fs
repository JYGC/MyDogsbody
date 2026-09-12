module MyDogsbody.Tests.Contracts.ListCalendarsDependencyContractTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.Util.Store
open MyDogsbody.Builders
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Startup

/// `ListCalendars` is the one dependency function type in this change whose real implementation
/// is a network service (friction #2). Per design.md's arrangement, "real" here means
/// `ListCalendars` exactly as the composition root binds it - `GoogleAccountApiFactory.bindListCalendars`:
/// the stored client secret, the account's stored token, `GoogleCalendarClient.listCalendarsVia` and
/// `toListCalendarsError` - over a temp Google.db, with only the calendar client's HTTP stubbed. That
/// exercises the adapter's own request-building, paging and response-parsing, and the translation
/// the page's alerts are written from. Live verification against Google itself is recorded as manual
/// coverage in outcome.md, not silently skipped.
///
/// Until PR review series 2 round 7 the "real" side here bound `listCalendarsVia` to a copy of that
/// translation written in this file, which never read the store and had no `CalendarApiNotEnabled`
/// or `ClientSecretInvalid` case. With the factory's translation of Google's answers swapped for the
/// store's, every test in the suite still passed.

type private RespondingHandler(respond: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        Task.FromResult(respond request)

type private StubHttpClientFactory(handler: HttpMessageHandler) =
    inherit Google.Apis.Http.HttpClientFactory()
    override _.CreateHandler(_args: Google.Apis.Http.CreateHttpClientArgs) : HttpMessageHandler = handler

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

// ---------- the composition root's binding, over a temp Google.db and a stubbed HttpMessageHandler ----------

let private handleError = HandleErrorBuilder(fun _ -> ())

let private sampleClientSecret =
    """{ "installed": { "client_id": "test-client-id", "client_secret": "test-client-secret" } }"""

/// A token `loadCredential` hands over without anything being asked of Google's token endpoint: its
/// access token is fresh, and it carries no refresh token. So a 401 from the stub reaches the adapter
/// as the Calendar API's own answer instead of prompting a refresh, which would go to Google's real
/// token endpoint. The one case that is about a refresh (`invalid_grant`) has its own credential below.
let private tokenNeedingNothingFromGoogle () =
    TokenResponse(AccessToken = "access-token", ExpiresInSeconds = Nullable 3599L, IssuedUtc = DateTime.UtcNow)

/// `ListCalendars` exactly as `GoogleAccountApiFactory` binds it, over a fresh temp Google.db holding
/// `clientSecret` and, when `authorised`, a token for `accountId` - with only the calendar client's
/// HTTP stubbed. `respond` sees every request that reaches "Google".
let private withBindingOver
    (clientSecret: string option)
    (authorised: bool)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    (test: ListCalendars -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        clientSecret
        |> Option.iter (fun secret ->
            match GoogleAccountStore.saveClientSecret handleError context.GetClientSecretCollection secret with
            | Ok() -> ()
            | Error ex -> failwith $"Test setup could not store the client secret: {ex.Message}")

        if authorised then
            let dataStore =
                GoogleCredentialDataStore.GoogleCredentialDataStore(handleError, context.GetCredentialCollection)
                :> IDataStore

            dataStore.StoreAsync(GoogleAccountId.value accountId, tokenNeedingNothingFromGoogle ())
            |> Async.AwaitTask
            |> Async.RunSynchronously

        let factory = StubHttpClientFactory(new RespondingHandler(respond)) :> Google.Apis.Http.IHttpClientFactory

        test (GoogleAccountApiFactory.bindListCalendars handleError context (GoogleCalendarClient.listCalendarsVia (Some factory)))
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private withRealListCalendars (respond: HttpRequestMessage -> HttpResponseMessage) (test: ListCalendars -> unit) =
    withBindingOver (Some sampleClientSecret) true respond test

/// The one case composed at the adapter rather than through the binding: a refresh Google refuses.
/// The binding's credential refreshes against Google's real token endpoint, so this case takes a
/// credential whose token endpoint is stubbed - and translates with the composition root's own
/// `toListCalendarsError`, not a copy of it.
let private withRealListCalendarsAs
    (credential: Google.Apis.Http.IConfigurableHttpClientInitializer)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    (test: ListCalendars -> unit)
    =
    let factory = StubHttpClientFactory(new RespondingHandler(respond)) :> Google.Apis.Http.IHttpClientFactory

    test (fun accountId ->
        GoogleCalendarClient.listCalendarsVia (Some factory) handleError credential ()
        |> Result.mapError (GoogleAccountApiMappers.toListCalendarsError accountId))

// ---------- the in-memory fake ----------

/// The calendars each authorised account has, and `NotAuthorised` for any other account - what the
/// binding reports for an account with no stored token.
let private withFakeListCalendars
    (calendarsByAccount: Map<GoogleAccountId, AvailableCalendar list>)
    (test: ListCalendars -> unit)
    =
    test (fun accountId ->
        match Map.tryFind accountId calendarsByAccount with
        | Some calendars -> Ok calendars
        | None -> Error(NotAuthorised accountId))

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"{label} expected Ok, but got Error: {error}"

// ---------- the shared suite: the binding and the fake, holding the same facts ----------

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real binding" |]; [| box "in-memory fake" |] ]

let private calendarListEntryFor (calendar: AvailableCalendar) =
    let primary = if calendar.IsPrimary then "true" else "false"
    let role = if calendar.IsPrimary then "owner" else "writer"

    $"""{{ "kind": "calendar#calendarListEntry", "id": "{CalendarId.value calendar.Id}", "summary": "{CalendarName.value calendar.Name}", "primary": {primary}, "accessRole": "{role}" }}"""

/// Either implementation, holding the same facts: `accountId` authorised with `calendars`, or (None)
/// not authorised at all. The binding is given Google's answer for those calendars through the stub;
/// the fake is given the calendars themselves. `requestsToGoogle` counts what reached the stub - always
/// 0 for the fake, which has no Google to reach.
let private withImplementation
    (name: string)
    (calendars: AvailableCalendar list option)
    (test: ListCalendars -> (unit -> int) -> unit)
    =
    match name with
    | "real binding" ->
        let requests = ref 0

        let respond (_: HttpRequestMessage) =
            requests.Value <- requests.Value + 1

            let entries =
                calendars |> Option.defaultValue [] |> List.map calendarListEntryFor |> String.concat ", "

            jsonResponse HttpStatusCode.OK (calendarListPage entries)

        withBindingOver (Some sampleClientSecret) calendars.IsSome respond (fun listCalendars ->
            test listCalendars (fun () -> requests.Value))
    | "in-memory fake" ->
        let calendarsByAccount =
            match calendars with
            | Some calendars -> Map.ofList [ accountId, calendars ]
            | None -> Map.empty

        withFakeListCalendars calendarsByAccount (fun listCalendars -> test listCalendars (fun () -> 0))
    | other -> failwith $"Unknown implementation '{other}'"

let private aCalendar id name isPrimary : AvailableCalendar =
    { Id = CalendarId.create id |> valueOrFail; Name = CalendarName.create name |> valueOrFail; IsPrimary = isPrimary }

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an authorised account's calendars are listed with every field`` (implementation: string) =
    let calendars =
        [ aCalendar "person@example.com" "person@example.com" true
          aCalendar "team@group.calendar.google.com" "Team Calendar" false ]

    withImplementation implementation (Some calendars) (fun listCalendars _ ->
        Assert.Equal(Ok calendars, listCalendars accountId))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an authorised account with no calendars lists none, which is not an error`` (implementation: string) =
    withImplementation implementation (Some []) (fun listCalendars _ -> Assert.Equal(Ok [], listCalendars accountId))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an account with no stored authorisation is NotAuthorised, carrying its id, without reaching Google`` (implementation: string) =
    withImplementation implementation None (fun listCalendars requestsToGoogle ->
        Assert.Equal(Error(NotAuthorised accountId), listCalendars accountId)
        Assert.Equal(0, requestsToGoogle ()))

// ---------- the real binding only: what the fake has no way to express ----------

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

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter lists only calendars the account can add events to`` () =
    // ListCalendars feeds the default-invoice-calendar picker and SetDefaultInvoiceCalendar's
    // existence check, so a calendar it returns is one invoice events can be written to. Google
    // leaves read-only calendars (holidays, birthdays, subscriptions) out only when asked to.
    let owned =
        """{ "kind": "calendar#calendarListEntry", "id": "primary", "summary": "My Calendar", "primary": true, "accessRole": "owner" }"""

    let holidays =
        """{ "kind": "calendar#calendarListEntry", "id": "holidays@group.v.calendar.google.com", "summary": "Holidays in Australia", "primary": false, "accessRole": "reader" }"""

    let respond (request: HttpRequestMessage) =
        if request.RequestUri.Query.Contains "minAccessRole=writer" then
            jsonResponse HttpStatusCode.OK (calendarListPage owned)
        else
            jsonResponse HttpStatusCode.OK (calendarListPage (owned + "," + holidays))

    withRealListCalendars respond (fun listCalendars ->
        let actual = listCalendars accountId |> okOrFail "listCalendars"
        let calendar = Assert.Single actual
        Assert.Equal("primary", CalendarId.value calendar.Id)
        Assert.Equal("My Calendar", CalendarName.value calendar.Name)
        Assert.True calendar.IsPrimary
    )

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter names a calendar the way the account's own list does`` () =
    // ListCalendars' names are what the picker shows. Google Calendar shows the user the name the
    // account gave a calendar (`summaryOverride`) over its owner's title (`summary`), and two shared
    // calendars with the same title are told apart only by those names.
    let renamed =
        """{ "kind": "calendar#calendarListEntry", "id": "alice@group.calendar.google.com", "summary": "Invoices", "summaryOverride": "Invoices - Alice", "primary": false, "accessRole": "writer" }"""

    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.OK (calendarListPage renamed)

    withRealListCalendars respond (fun listCalendars ->
        let actual = listCalendars accountId |> okOrFail "listCalendars"
        let calendar = Assert.Single actual
        Assert.Equal("alice@group.calendar.google.com", CalendarId.value calendar.Id)
        Assert.Equal("Invoices - Alice", CalendarName.value calendar.Name)
        Assert.False calendar.IsPrimary
    )

[<Fact; Trait("Level", "Contract")>]
let ``the real binding reports a project with the Calendar API switched off as CalendarApiNotEnabled, carrying Google's sentence`` () =
    // A 403 re-authorising can never fix: the Cloud project behind the client secret has the Calendar
    // API switched off. The composition root keeps it as its own case, carrying Google's sentence,
    // which names the project and the URL that enables it.
    let googleSentence =
        "Google Calendar API has not been used in project 000000000000 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/calendar-json.googleapis.com/overview?project=000000000000 then retry."

    let respond (_: HttpRequestMessage) =
        jsonResponse
            HttpStatusCode.Forbidden
            $"""{{ "error": {{ "code": 403, "message": "{googleSentence}", "errors": [ {{ "domain": "usageLimits", "reason": "accessNotConfigured", "message": "{googleSentence}" }} ] }} }}"""

    withRealListCalendars respond (fun listCalendars ->
        match listCalendars accountId with
        | Error(CalendarApiNotEnabled message) ->
            Assert.Equal($"{GoogleCalendarClient.apiNotEnabledPrefix} {googleSentence}", message)
        | other -> Assert.Fail $"Expected CalendarApiNotEnabled carrying Google's sentence, got %A{other}")

[<Fact; Trait("Level", "Contract")>]
let ``the real binding reports a malformed stored client secret as ClientSecretInvalid, without reaching Google`` () =
    // The binding parses the stored secret before anything is sent, so a bad paste is the user's to
    // fix by pasting again - not "Google could not be reached".
    let requests = ref 0

    let respond (_: HttpRequestMessage) =
        requests.Value <- requests.Value + 1
        jsonResponse HttpStatusCode.OK (calendarListPage "")

    withBindingOver (Some "not json at all") true respond (fun listCalendars ->
        Assert.Equal(Error(ClientSecretInvalid "The stored Google client secret is malformed."), listCalendars accountId)
        Assert.Equal(0, requests.Value))

[<Fact; Trait("Level", "Contract")>]
let ``the real binding reports a missing client secret as ClientSecretMissing, without reaching Google`` () =
    let requests = ref 0

    let respond (_: HttpRequestMessage) =
        requests.Value <- requests.Value + 1
        jsonResponse HttpStatusCode.OK (calendarListPage "")

    withBindingOver None true respond (fun listCalendars ->
        Assert.Equal(Error ClientSecretMissing, listCalendars accountId)
        Assert.Equal(0, requests.Value))
