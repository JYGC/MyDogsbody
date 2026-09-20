module MyDogsbody.Tests.Contracts.CreateCalendarEventDependencyContractTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
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

/// `CreateCalendarEvent` - see `ListCalendarEventsDependencyContractTests.fs`'s own header for the
/// shape this suite follows: "real" means `GoogleAccountApiFactory.bindCreateCalendarEvent` - the
/// stored client secret, the account's stored token, `GoogleCalendarClient.createEventVia` and
/// `GoogleAccountApiMappers.toCreateCalendarEventError` - over a temp Google.db, with only the
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
let private fieldSeparatorChar = char 0x1F
let private syncKey = InvoiceSyncKey.parse $"supplier-1{fieldSeparatorChar}INV-100" |> valueOrFail

let private allDayEvent: AllDayEvent =
    { Date = DateTime(2026, 9, 20); Title = "Invoice due: INV-100"; Description = "Amount: 100 AUD." }

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
    (test: CreateCalendarEvent -> unit)
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

        test (GoogleAccountApiFactory.bindCreateCalendarEvent handleError context (GoogleCalendarClient.createEventVia (Some factory)))
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private withRealCreateCalendarEvent (respond: HttpRequestMessage -> HttpResponseMessage) (test: CreateCalendarEvent -> unit) =
    withBindingOver (Some sampleClientSecret) true respond test

// ---------- the in-memory fake ----------

let private withFakeCreateCalendarEvent
    (authorisedAccountIds: Set<GoogleAccountId>)
    (returnedEventId: CalendarEventId)
    (test: CreateCalendarEvent -> unit)
    =
    test (fun testAccountId _calendarId _syncKey _allDayEvent ->
        if Set.contains testAccountId authorisedAccountIds then Ok returnedEventId else Error(NotAuthorised testAccountId))

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"{label} expected Ok, but got Error: {error}"

// ---------- the shared suite: the binding and the fake, holding the same facts ----------

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real binding" |]; [| box "in-memory fake" |] ]

let private withImplementation
    (name: string)
    (authorised: bool)
    (returnedEventId: CalendarEventId)
    (test: CreateCalendarEvent -> unit)
    =
    match name with
    | "real binding" ->
        let respond (_: HttpRequestMessage) =
            jsonResponse
                HttpStatusCode.OK
                $"""{{ "kind": "calendar#event", "id": "{CalendarEventId.value returnedEventId}", "start": {{ "date": "2026-09-20" }}, "end": {{ "date": "2026-09-21" }} }}"""

        withBindingOver (Some sampleClientSecret) authorised respond test
    | "in-memory fake" ->
        let authorisedAccountIds = if authorised then Set.singleton accountId else Set.empty
        withFakeCreateCalendarEvent authorisedAccountIds returnedEventId test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an authorised account's event is created and its id returned`` (implementation: string) =
    let expectedId = CalendarEventId.create "created-evt-1" |> valueOrFail

    withImplementation implementation true expectedId (fun createCalendarEvent ->
        Assert.Equal(Ok expectedId, createCalendarEvent accountId calendarId syncKey allDayEvent))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an account with no stored authorisation is NotAuthorised, carrying its id`` (implementation: string) =
    let expectedId = CalendarEventId.create "unused" |> valueOrFail

    withImplementation implementation false expectedId (fun createCalendarEvent ->
        Assert.Equal(Error(NotAuthorised accountId), createCalendarEvent accountId calendarId syncKey allDayEvent))

// ---------- the real binding only: what the fake has no way to express ----------

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter sends an all-day start date and stamps the derived sync key`` () =
    let mutable capturedRequestBody = ""

    let readGZipCompressedRequestBodyAsText (request: HttpRequestMessage) : string =
        let compressedBytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        use compressedStream = new IO.MemoryStream(compressedBytes)
        use decompressingStream = new IO.Compression.GZipStream(compressedStream, IO.Compression.CompressionMode.Decompress)
        use reader = new IO.StreamReader(decompressingStream, Text.Encoding.UTF8)
        reader.ReadToEnd()

    let respond (request: HttpRequestMessage) =
        capturedRequestBody <- readGZipCompressedRequestBodyAsText request

        jsonResponse
            HttpStatusCode.OK
            """{ "kind": "calendar#event", "id": "created-evt-1", "start": { "date": "2026-09-20" }, "end": { "date": "2026-09-21" } }"""

    withRealCreateCalendarEvent respond (fun createCalendarEvent ->
        createCalendarEvent accountId calendarId syncKey allDayEvent |> okOrFail "createCalendarEvent" |> ignore

        let requestRoot = JsonDocument.Parse(capturedRequestBody).RootElement
        let startElement = requestRoot.GetProperty "start"
        Assert.Equal("2026-09-20", startElement.GetProperty("date").GetString())
        Assert.DoesNotContain("dateTime", startElement.EnumerateObject() |> Seq.map (fun p -> p.Name))

        let privateProperties = requestRoot.GetProperty("extendedProperties").GetProperty "private"
        let syncKeyPropertyOnTheRequest =
            privateProperties.GetProperty(InvoiceSyncKey.PrivateExtendedPropertyNameOnAGoogleCalendarEvent).GetString()

        Assert.Equal(InvoiceSyncKey.value syncKey, syncKeyPropertyOnTheRequest))

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 400 rejection to EventRejected, carrying Google's sentence`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.BadRequest """{ "error": { "code": 400, "message": "Invalid summary value.", "errors": [] } }"""

    withRealCreateCalendarEvent respond (fun createCalendarEvent ->
        match createCalendarEvent accountId calendarId syncKey allDayEvent with
        | Error(EventRejected reason) -> Assert.Contains("Invalid summary value.", reason)
        | other -> Assert.Fail($"Expected EventRejected, got {other}"))

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 403 to NotAuthorised carrying the account id`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.Forbidden """{ "error": { "code": 403, "message": "Permission denied", "errors": [] } }"""

    withRealCreateCalendarEvent respond (fun createCalendarEvent ->
        Assert.Equal(Error(NotAuthorised accountId), createCalendarEvent accountId calendarId syncKey allDayEvent))

[<Fact; Trait("Level", "Contract")>]
let ``the real adapter maps a 429 to CalendarRateLimited, distinct from NotAuthorised`` () =
    let respond (_: HttpRequestMessage) =
        jsonResponse HttpStatusCode.TooManyRequests """{ "error": { "code": 429, "message": "Rate Limit Exceeded", "errors": [] } }"""

    withRealCreateCalendarEvent respond (fun createCalendarEvent ->
        match createCalendarEvent accountId calendarId syncKey allDayEvent with
        | Error(CalendarRateLimited _) -> ()
        | other -> Assert.Fail($"Expected CalendarRateLimited, got {other}"))
