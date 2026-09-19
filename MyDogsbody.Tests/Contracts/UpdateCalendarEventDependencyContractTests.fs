module MyDogsbody.Tests.Contracts.UpdateCalendarEventDependencyContractTests

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

/// `UpdateCalendarEvent` - see `ListCalendarEventsDependencyContractTests.fs`'s own header for the
/// shape this suite follows: "real" means `GoogleAccountApiFactory.bindUpdateCalendarEvent` - the
/// stored client secret, the account's stored token, `GoogleCalendarClient.updateEventVia` and
/// `GoogleAccountApiMappers.toUpdateCalendarEventError` - over a temp Google.db, with only the
/// calendar client's HTTP stubbed.

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

let private accountId = GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail
let private calendarId = CalendarId.create "cal-1" |> valueOrFail
let private eventId = CalendarEventId.create "evt-1" |> valueOrFail

let private allDayEvent: AllDayEvent =
    { Date = DateTime(2026, 9, 21); Title = "Invoice due: INV-100 (updated)"; Description = "" }

// ---------- the composition root's binding, over a temp Google.db and a stubbed HttpMessageHandler ----------

let private handleError = HandleErrorBuilder(fun _ -> ())

let private sampleClientSecret =
    """{ "installed": { "client_id": "test-client-id", "client_secret": "test-client-secret" } }"""

let private tokenNeedingNothingFromGoogle () =
    TokenResponse(AccessToken = "access-token", ExpiresInSeconds = Nullable 3599L, IssuedUtc = DateTime.UtcNow)

let private withBindingOver
    (clientSecret: string option)
    (authorised: bool)
    (respond: HttpRequestMessage -> HttpResponseMessage)
    (test: UpdateCalendarEvent -> unit)
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

        test (GoogleAccountApiFactory.bindUpdateCalendarEvent handleError context (GoogleCalendarClient.updateEventVia (Some factory)))
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private withRealUpdateCalendarEvent (respond: HttpRequestMessage -> HttpResponseMessage) (test: UpdateCalendarEvent -> unit) =
    withBindingOver (Some sampleClientSecret) true respond test

// ---------- the in-memory fake ----------

let private withFakeUpdateCalendarEvent
    (authorisedAccountIds: Set<GoogleAccountId>)
    (existingEventIds: Set<CalendarEventId>)
    (test: UpdateCalendarEvent -> unit)
    =
    test (fun testAccountId _calendarId testEventId _allDayEvent ->
        if not (Set.contains testAccountId authorisedAccountIds) then
            Error(NotAuthorised testAccountId)
        elif not (Set.contains testEventId existingEventIds) then
            Error(EventNoLongerExists testEventId)
        else
            Ok())

// ---------- the shared suite: the binding and the fake, holding the same facts ----------

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real binding" |]; [| box "in-memory fake" |] ]

let private withImplementation
    (name: string)
    (authorised: bool)
    (eventExists: bool)
    (test: UpdateCalendarEvent -> unit)
    =
    match name with
    | "real binding" ->
        let respond (_: HttpRequestMessage) =
            if not eventExists then
                jsonResponse HttpStatusCode.NotFound """{ "error": { "code": 404, "message": "Not Found", "errors": [] } }"""
            else
                jsonResponse
                    HttpStatusCode.OK
                    """{ "kind": "calendar#event", "id": "evt-1", "start": { "date": "2026-09-21" }, "end": { "date": "2026-09-22" } }"""

        withBindingOver (Some sampleClientSecret) authorised respond test
    | "in-memory fake" ->
        let authorisedAccountIds = if authorised then Set.singleton accountId else Set.empty
        let existingEventIds = if eventExists then Set.singleton eventId else Set.empty
        withFakeUpdateCalendarEvent authorisedAccountIds existingEventIds test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an authorised account's event is updated`` (implementation: string) =
    withImplementation implementation true true (fun updateCalendarEvent ->
        Assert.Equal(Ok(), updateCalendarEvent accountId calendarId eventId allDayEvent))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``updating an event Google no longer has is EventNoLongerExists, carrying its id`` (implementation: string) =
    withImplementation implementation true false (fun updateCalendarEvent ->
        Assert.Equal(Error(EventNoLongerExists eventId), updateCalendarEvent accountId calendarId eventId allDayEvent))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an account with no stored authorisation is NotAuthorised, carrying its id`` (implementation: string) =
    withImplementation implementation false true (fun updateCalendarEvent ->
        Assert.Equal(Error(NotAuthorised accountId), updateCalendarEvent accountId calendarId eventId allDayEvent))

// ---------- the real binding only: what the fake has no way to express ----------

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 400 rejection to EventRejected, carrying Google's sentence`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.BadRequest """{ "error": { "code": 400, "message": "Invalid summary value.", "errors": [] } }"""

    withRealUpdateCalendarEvent respond (fun updateCalendarEvent ->
        match updateCalendarEvent accountId calendarId eventId allDayEvent with
        | Error(EventRejected reason) -> Assert.Contains("Invalid summary value.", reason)
        | other -> Assert.Fail($"Expected EventRejected, got {other}"))

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 429 to CalendarRateLimited, distinct from NotAuthorised`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.TooManyRequests """{ "error": { "code": 429, "message": "Rate Limit Exceeded", "errors": [] } }"""

    withRealUpdateCalendarEvent respond (fun updateCalendarEvent ->
        match updateCalendarEvent accountId calendarId eventId allDayEvent with
        | Error(CalendarRateLimited _) -> ()
        | other -> Assert.Fail($"Expected CalendarRateLimited, got {other}"))
