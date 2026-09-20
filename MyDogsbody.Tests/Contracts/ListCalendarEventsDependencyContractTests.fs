module MyDogsbody.Tests.Contracts.ListCalendarEventsDependencyContractTests

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

/// `ListCalendarEvents` is change #7's first events-half dependency function type whose real
/// implementation is a network service. Same arrangement as `ListCalendarsDependencyContractTests`
/// (read that file's own header first): "real" here means `GoogleAccountApiFactory.bindListCalendarEvents`
/// - the stored client secret, the account's stored token, `GoogleCalendarClient.listEventsVia` and
/// `GoogleAccountApiMappers.toListCalendarEventsError` - over a temp Google.db, with only the
/// calendar client's HTTP stubbed. Live verification against Google itself is recorded as manual
/// coverage in outcome.md, not silently skipped.

type private RespondingHandler(respond: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        Task.FromResult(respond request)

type private StubHttpClientFactory(handler: HttpMessageHandler) =
    inherit Google.Apis.Http.HttpClientFactory()
    override _.CreateHandler(_arguments: Google.Apis.Http.CreateHttpClientArgs) : HttpMessageHandler = handler

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private jsonResponse (statusCode: HttpStatusCode) (body: string) =
    let response = new HttpResponseMessage(statusCode)
    response.Content <- new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    response

let private eventsPage (items: string) =
    $"""{{ "kind": "calendar#events", "items": [ {items} ] }}"""

/// The same ASCII Unit Separator encoding `GoogleCalendarClientTests.fs` documents its own
/// `jsonEscapedSyncKeyValue` as needing - a hand-written JSON body embedding an `InvoiceSyncKey`'s
/// raw separator character has to escape it, or the stub response is not valid JSON.
let private fieldSeparatorChar = char 0x1F

let private jsonEscapedSyncKeyValue (syncKey: InvoiceSyncKey) =
    (InvoiceSyncKey.value syncKey).Replace(string fieldSeparatorChar, "\\u001f")

let private eventEntryFor (event: CalendarEvent) =
    let startDate = event.Event.Date.ToString("yyyy-MM-dd")
    let endDate = event.Event.Date.AddDays(1.0).ToString("yyyy-MM-dd")

    let extendedProperties =
        match event.SyncKey with
        | Some key ->
            $""", "extendedProperties": {{ "private": {{ "{InvoiceSyncKey.PrivateExtendedPropertyNameOnAGoogleCalendarEvent}": "{jsonEscapedSyncKeyValue key}" }} }}"""
        | None -> ""

    $"""{{ "kind": "calendar#event", "id": "{CalendarEventId.value event.Id}", "summary": "{event.Event.Title}", "description": "{event.Event.Description}", "start": {{ "date": "{startDate}" }}, "end": {{ "date": "{endDate}" }}{extendedProperties} }}"""

let private accountId = GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail
let private calendarId = CalendarId.create "cal-1" |> valueOrFail

let private testDateRange =
    CalendarDateRange.create (DateTime(2026, 9, 1)) (DateTime(2026, 9, 30)) |> valueOrFail

let private aCalendarEvent (id: string) (title: string) (date: DateTime) (syncKey: InvoiceSyncKey option) : CalendarEvent =
    { Id = CalendarEventId.create id |> valueOrFail
      Event = { Date = date; Title = title; Description = "" }
      SyncKey = syncKey }

let private aSyncKey (reference: string) =
    InvoiceSyncKey.parse $"supplier-1{fieldSeparatorChar}{reference}" |> valueOrFail

// ---------- the composition root's binding, over a temp Google.db and a stubbed HttpMessageHandler ----------

let private handleError = HandleErrorBuilder(fun _ -> ())

let private sampleClientSecret =
    """{ "installed": { "client_id": "test-client-id", "client_secret": "test-client-secret" } }"""

let private tokenNeedingNothingFromGoogle () =
    TokenResponse(AccessToken = "access-token", ExpiresInSeconds = Nullable 3599L, IssuedUtc = DateTime.UtcNow)

/// `ListCalendarEvents` exactly as `GoogleAccountApiFactory` binds it, over a fresh temp Google.db.
let private withBindingOver
    (clientSecret: string option)
    (authorised: bool)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    (test: ListCalendarEvents -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        clientSecret
        |> Option.iter (fun secret ->
            match GoogleAccountStore.saveClientSecret handleError context.GetClientSecretCollection secret with
            | Ok() -> ()
            | Error capturedException -> failwith $"Test setup could not store the client secret: {capturedException.Message}")

        if authorised then
            let dataStore =
                GoogleCredentialDataStore.GoogleCredentialDataStore(handleError, context.GetCredentialCollection)
                :> IDataStore

            dataStore.StoreAsync(GoogleAccountId.value accountId, tokenNeedingNothingFromGoogle ())
            |> Async.AwaitTask
            |> Async.RunSynchronously

        let factory = StubHttpClientFactory(new RespondingHandler(respond)) :> Google.Apis.Http.IHttpClientFactory

        test (GoogleAccountApiFactory.bindListCalendarEvents handleError context (GoogleCalendarClient.listEventsVia (Some factory)))
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private withRealListCalendarEvents (respond: HttpRequestMessage -> HttpResponseMessage) (test: ListCalendarEvents -> unit) =
    withBindingOver (Some sampleClientSecret) true respond test

// ---------- the in-memory fake ----------

let private withFakeListCalendarEvents
    (eventsByAccountAndCalendar: Map<GoogleAccountId * CalendarId, CalendarEvent list>)
    (test: ListCalendarEvents -> unit)
    =
    test (fun testAccountId testCalendarId _range ->
        match Map.tryFind (testAccountId, testCalendarId) eventsByAccountAndCalendar with
        | Some events -> Ok events
        | None -> Error(NotAuthorised testAccountId))

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"{label} expected Ok, but got Error: {error}"

// ---------- the shared suite: the binding and the fake, holding the same facts ----------

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real binding" |]; [| box "in-memory fake" |] ]

let private withImplementation
    (name: string)
    (events: CalendarEvent list option)
    (test: ListCalendarEvents -> (unit -> int) -> unit)
    =
    match name with
    | "real binding" ->
        let requests = ref 0

        let respond (_: HttpRequestMessage) =
            requests.Value <- requests.Value + 1
            let entries = events |> Option.defaultValue [] |> List.map eventEntryFor |> String.concat ", "
            jsonResponse HttpStatusCode.OK (eventsPage entries)

        withBindingOver (Some sampleClientSecret) events.IsSome respond (fun listCalendarEvents ->
            test listCalendarEvents (fun () -> requests.Value))
    | "in-memory fake" ->
        let eventsMap =
            match events with
            | Some evs -> Map.ofList [ (accountId, calendarId), evs ]
            | None -> Map.empty

        withFakeListCalendarEvents eventsMap (fun listCalendarEvents -> test listCalendarEvents (fun () -> 0))
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an authorised account's calendar events are listed with every field`` (implementation: string) =
    let syncedEvent = aCalendarEvent "evt-1" "Invoice due: INV-100" (DateTime(2026, 9, 20)) (Some(aSyncKey "INV-100"))
    let handAddedEvent = aCalendarEvent "evt-2" "Team lunch" (DateTime(2026, 9, 21)) None

    withImplementation implementation (Some [ syncedEvent; handAddedEvent ]) (fun listCalendarEvents _ ->
        let actual = listCalendarEvents accountId calendarId testDateRange |> okOrFail "listCalendarEvents"
        Assert.Equal<CalendarEvent list>([ syncedEvent; handAddedEvent ], actual))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an account with no matching events lists none, which is not an error`` (implementation: string) =
    withImplementation implementation (Some []) (fun listCalendarEvents _ ->
        Assert.Equal(Ok [], listCalendarEvents accountId calendarId testDateRange))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an account with no stored authorisation is NotAuthorised, carrying its id, without reaching Google`` (implementation: string) =
    withImplementation implementation None (fun listCalendarEvents requestsToGoogle ->
        Assert.Equal(Error(NotAuthorised accountId), listCalendarEvents accountId calendarId testDateRange)
        Assert.Equal(0, requestsToGoogle ()))

// ---------- the real binding only: what the fake has no way to express ----------

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter reports an event carrying the extended property with SyncKey Some, and one without it as None`` () =
    let key = aSyncKey "INV-100"

    let respond (_: HttpRequestMessage) =
        let withKey =
            $"""{{ "kind": "calendar#event", "id": "evt-1", "summary": "Invoice due: INV-100", "description": "", "start": {{ "date": "2026-09-20" }}, "end": {{ "date": "2026-09-21" }}, "extendedProperties": {{ "private": {{ "{InvoiceSyncKey.PrivateExtendedPropertyNameOnAGoogleCalendarEvent}": "{jsonEscapedSyncKeyValue key}" }} }} }}"""

        let withoutKey =
            """{ "kind": "calendar#event", "id": "evt-2", "summary": "Team lunch", "description": "", "start": { "date": "2026-09-21" }, "end": { "date": "2026-09-22" } }"""

        jsonResponse HttpStatusCode.OK (eventsPage (withKey + "," + withoutKey))

    withRealListCalendarEvents respond (fun listCalendarEvents ->
        let actual = listCalendarEvents accountId calendarId testDateRange |> okOrFail "listCalendarEvents"
        let withKeyEvent = actual |> List.find (fun e -> CalendarEventId.value e.Id = "evt-1")
        let withoutKeyEvent = actual |> List.find (fun e -> CalendarEventId.value e.Id = "evt-2")
        Assert.Equal(Some key, withKeyEvent.SyncKey)
        Assert.Equal(None, withoutKeyEvent.SyncKey))

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter returns events from every page`` () =
    let respond (request: HttpRequestMessage) =
        if request.RequestUri.Query.Contains "pageToken=page-2" then
            jsonResponse
                HttpStatusCode.OK
                """{ "kind": "calendar#events", "items": [ { "kind": "calendar#event", "id": "evt-2", "summary": "Second page", "description": "", "start": { "date": "2026-09-21" }, "end": { "date": "2026-09-22" } } ] }"""
        else
            jsonResponse
                HttpStatusCode.OK
                """{ "kind": "calendar#events", "nextPageToken": "page-2", "items": [ { "kind": "calendar#event", "id": "evt-1", "summary": "First page", "description": "", "start": { "date": "2026-09-20" }, "end": { "date": "2026-09-21" } } ] }"""

    withRealListCalendarEvents respond (fun listCalendarEvents ->
        let actual = listCalendarEvents accountId calendarId testDateRange |> okOrFail "listCalendarEvents"
        let ids = actual |> List.map (fun e -> CalendarEventId.value e.Id) |> List.sort
        Assert.Equal<string list>([ "evt-1"; "evt-2" ], ids))

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 403 to NotAuthorised carrying the account id`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.Forbidden """{ "error": { "code": 403, "message": "Permission denied", "errors": [] } }"""

    withRealListCalendarEvents respond (fun listCalendarEvents ->
        Assert.Equal(Error(NotAuthorised accountId), listCalendarEvents accountId calendarId testDateRange))

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 429 to CalendarRateLimited, distinct from NotAuthorised`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.TooManyRequests """{ "error": { "code": 429, "message": "Rate Limit Exceeded", "errors": [] } }"""

    withRealListCalendarEvents respond (fun listCalendarEvents ->
        match listCalendarEvents accountId calendarId testDateRange with
        | Error(CalendarRateLimited _) -> ()
        | other -> Assert.Fail($"Expected CalendarRateLimited, got {other}"))

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 410 to CalendarNoLongerExists carrying the calendar id`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.Gone """{ "error": { "code": 410, "message": "Resource has been deleted", "errors": [] } }"""

    withRealListCalendarEvents respond (fun listCalendarEvents ->
        Assert.Equal(Error(CalendarNoLongerExists calendarId), listCalendarEvents accountId calendarId testDateRange))
