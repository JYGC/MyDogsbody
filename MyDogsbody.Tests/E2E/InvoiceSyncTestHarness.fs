module MyDogsbody.Tests.E2E.InvoiceSyncTestHarness

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Bunit
open Microsoft.Data.Sqlite
open Microsoft.Extensions.DependencyInjection
open MudBlazor.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// The composition root down to a real main SQLite database and a real Google.db, back into
// InvoiceSyncApi - with only the Google Calendar HTTP call stubbed, the same seam
// GoogleCalendarClientTests.fs and the Phase 9 contract suites use, over a small in-memory fake
// Google Calendar (FakeGoogleCalendar below) that behaves like the real REST API closely enough
// for these flows: list/insert/update/delete, round-tripping the extended property.

type private RespondingHandler(respond: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        Task.FromResult(respond request)

type private StubHttpClientFactory(handler: HttpMessageHandler) =
    inherit Google.Apis.Http.HttpClientFactory()
    override _.CreateHandler(_arguments: Google.Apis.Http.CreateHttpClientArgs) : HttpMessageHandler = handler

/// `BaseClientService.Initializer.GZipEnabled` defaults to `true`, so every write request the real
/// adapter sends arrives here already GZip-compressed (GoogleCalendarClientTests.fs found this
/// first). A GET has no body, so only POST/PUT need decompressing.
let private readRequestBodyAsText (request: HttpRequestMessage) : string =
    let compressedBytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    use compressedStream = new MemoryStream(compressedBytes)
    use decompressingStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress)
    use reader = new StreamReader(decompressingStream, Text.Encoding.UTF8)
    reader.ReadToEnd()

let private jsonResponse (statusCode: HttpStatusCode) (body: string) =
    let response = new HttpResponseMessage(statusCode)
    response.Content <- new StringContent(body, Text.Encoding.UTF8, "application/json")
    response

let private tryGetProperty (name: string) (element: JsonElement) : JsonElement option =
    match element.TryGetProperty name with
    | true, value -> Some value
    | false, _ -> None

/// A minimal in-memory stand-in for one Google calendar's events, kept per test. Understands
/// enough of the real REST shape (Events.list/insert/update/delete) to round-trip a create through
/// a later list, and to answer update/delete against an id it does or does not still hold - which
/// is what lets an E2E test prove "up to date" and "already gone" without a real network call.
type FakeGoogleCalendar() =
    let events = System.Collections.Generic.Dictionary<string, string * string * string option>()
    let mutable nextEventId = 0

    /// Seeds an event directly, bypassing Insert - for a test that wants a pre-existing calendar
    /// state before the flow under test even starts.
    member _.Seed(id: string, title: string, dateText: string, syncKeyValue: string option) =
        events.[id] <- (title, dateText, syncKeyValue)

    member _.Count = events.Count
    member _.Contains(id: string) = events.ContainsKey id

    member private _.ExtendedPropertyValue(root: JsonElement) : string option =
        tryGetProperty "extendedProperties" root
        |> Option.bind (tryGetProperty "private")
        |> Option.bind (tryGetProperty InvoiceSyncKey.PropertyName)
        |> Option.map (fun value -> value.GetString())

    member this.Respond(request: HttpRequestMessage) : HttpResponseMessage =
        let path = request.RequestUri.AbsolutePath

        if request.Method = HttpMethod.Get && path.EndsWith "/events" then
            let items =
                events
                |> Seq.map (fun entry ->
                    let id = entry.Key
                    let title, dateText, syncKeyValue = entry.Value

                    let extendedProperties =
                        match syncKeyValue with
                        | Some value -> $"""{{ "private": {{ "{InvoiceSyncKey.PropertyName}": "{value}" }} }}"""
                        | None -> "null"

                    $"""{{ "id": "{id}", "summary": "{title}", "start": {{ "date": "{dateText}" }}, "extendedProperties": {extendedProperties} }}""")
                |> String.concat ","

            jsonResponse HttpStatusCode.OK $"""{{ "kind": "calendar#events", "items": [ {items} ] }}"""

        elif request.Method = HttpMethod.Post && path.EndsWith "/events" then
            use document = JsonDocument.Parse(readRequestBodyAsText request)
            let root = document.RootElement
            let title = root.GetProperty("summary").GetString()
            let dateText = root.GetProperty("start").GetProperty("date").GetString()
            let syncKeyValue = this.ExtendedPropertyValue root

            nextEventId <- nextEventId + 1
            let id = $"evt-{nextEventId}"
            events.[id] <- (title, dateText, syncKeyValue)

            jsonResponse HttpStatusCode.OK $"""{{ "id": "{id}", "summary": "{title}", "start": {{ "date": "{dateText}" }} }}"""

        // PATCH, not Put: GoogleCalendarClient.updateEventVia issues a PATCH so fields it does not
        // set - extendedProperties among them - survive server-side rather than being cleared by a
        // full-resource PUT (PR #23 review round 3).
        elif request.Method = HttpMethod.Patch && path.Contains "/events/" then
            let id = path.Substring(path.LastIndexOf '/' + 1)

            if events.ContainsKey id then
                use document = JsonDocument.Parse(readRequestBodyAsText request)
                let root = document.RootElement
                let title = root.GetProperty("summary").GetString()
                let dateText = root.GetProperty("start").GetProperty("date").GetString()
                let _, _, syncKeyValue = events.[id]
                events.[id] <- (title, dateText, syncKeyValue)

                jsonResponse HttpStatusCode.OK $"""{{ "id": "{id}", "summary": "{title}", "start": {{ "date": "{dateText}" }} }}"""
            else
                jsonResponse HttpStatusCode.NotFound """{ "error": { "code": 404, "message": "Not Found" } }"""

        elif request.Method = HttpMethod.Delete && path.Contains "/events/" then
            let id = path.Substring(path.LastIndexOf '/' + 1)

            if events.Remove id then
                jsonResponse HttpStatusCode.NoContent ""
            else
                jsonResponse HttpStatusCode.NotFound """{ "error": { "code": 404, "message": "Not Found" } }"""

        else
            jsonResponse HttpStatusCode.NotFound "{}"

type InvoiceSyncHarness
    (
        invoiceApi: InvoiceApi,
        scanWindowApi: ScanWindowApi,
        invoiceSyncApi: InvoiceSyncApi,
        calendar: FakeGoogleCalendar,
        mainConnectionString: string,
        logged: ResizeArray<MyDogsbodyException>
    ) =
    inherit TestContext()

    do
        base.Services.AddMudServices() |> ignore
        base.Services.AddSingleton<InvoiceApi>(invoiceApi) |> ignore
        base.Services.AddSingleton<ScanWindowApi>(scanWindowApi) |> ignore
        base.Services.AddSingleton<InvoiceSyncApi>(invoiceSyncApi) |> ignore
        base.JSInterop.Mode <- JSRuntimeMode.Loose

    member _.InvoiceApi = invoiceApi
    member _.ScanWindowApi = scanWindowApi
    member _.InvoiceSyncApi = invoiceSyncApi
    member _.Calendar = calendar
    member _.Logged = logged

    /// Seeds an invoice row directly - the ledger is not this flow's own concern (change #4
    /// covers scanning); a sync test only needs rows already in the ledger.
    member _.ExecInvoiceLedgerSql(sql: string) =
        use connection = new SqliteConnection(mainConnectionString)
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private sampleClientSecret =
    """{ "installed": { "client_id": "test-client-id", "client_secret": "test-client-secret" } }"""

let private tokenNeedingNothingFromGoogle () =
    Google.Apis.Auth.OAuth2.Responses.TokenResponse(
        AccessToken = "access-token",
        ExpiresInSeconds = Nullable 3599L,
        IssuedUtc = DateTime.UtcNow
    )

/// The Google account id, calendar id and clock every flow test shares - a ready account, chosen
/// once here rather than repeated per test.
// LiteDB's ObjectId constructor demands a real 24-hex-char id (its underlying MongoDB-style
// binary format) - an arbitrary string throws when the entity is mapped, which is what a plain
// "e2e-google-account" did here at first.
let readyAccountId = GoogleAccountId.create "507f1f77bcf86cd799439099" |> valueOrFail
let readyCalendarId = CalendarId.create "e2e-calendar" |> valueOrFail
let clock () = DateTime(2026, 6, 15, 12, 0, 0)

/// The real composition root over a real temp main SQLite database (schema by the real
/// migrations), a real temp Google.db seeded with a client secret, a stored token and a ready
/// registered account, and a real temp Thunderbird LiteDB (unused by this flow, present only
/// because InvoiceApiFactory needs one). Only the Google Calendar HTTP call is stubbed, against
/// `calendar`'s in-memory state.
let withInvoiceSyncHarness (calendar: FakeGoogleCalendar) (test: InvoiceSyncHarness -> unit) =
    let mainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let googleDatabasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let thunderbirdDatabasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let mainConnectionString = $"Data Source={mainPath};Pooling=False"
    MigrationSetup.setupMigrations mainConnectionString
    let mainContext = DatabaseContextSetup.createDatabaseContext mainPath
    let googleContext = GoogleDatabaseContextModule.getDatabaseContext googleDatabasePath "direct"

    let thunderbirdDatabaseContext =
        ThunderbirdDatabaseContextModule.getDatabaseContext thunderbirdDatabasePath "direct"

    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add

    GoogleAccountStore.saveClientSecret handleError googleContext.GetClientSecretCollection sampleClientSecret
    |> function
        | Ok() -> ()
        | Error caughtException -> failwith $"Test setup could not store the client secret: {caughtException.Message}"

    let dataStore =
        GoogleCredentialDataStore.GoogleCredentialDataStore(handleError, googleContext.GetCredentialCollection)
        :> Google.Apis.Util.Store.IDataStore

    dataStore.StoreAsync(GoogleAccountId.value readyAccountId, tokenNeedingNothingFromGoogle ())
    |> Async.AwaitTask
    |> Async.RunSynchronously

    let readyAccount: RegisteredGoogleAccount =
        { Id = readyAccountId
          EmailAddress = GoogleEmail.create "e2e@example.com" |> valueOrFail
          DefaultInvoiceCalendar = Some readyCalendarId
          NeedsReauthorisation = false }

    GoogleAccountStore.saveOne handleError googleContext.GetAccountCollection readyAccount
    |> function
        | Ok _ -> ()
        | Error caughtException -> failwith $"Test setup could not save the ready account: {caughtException.Message}"

    let factory = new StubHttpClientFactory(new RespondingHandler(calendar.Respond)) :> Google.Apis.Http.IHttpClientFactory

    let invoiceApi = InvoiceApiFactory.createInvoiceApi handleError clock mainContext thunderbirdDatabaseContext
    let scanWindowApi = ScanWindowApiFactory.createScanWindowApi handleError mainContext

    let invoiceSyncApi =
        InvoiceSyncApiFactory.createInvoiceSyncApiWith
            handleError
            clock
            mainContext
            googleContext
            (GoogleCalendarClient.listEventsVia (Some factory))
            (GoogleCalendarClient.createEventVia (Some factory))
            (GoogleCalendarClient.updateEventVia (Some factory))
            (GoogleCalendarClient.deleteEventVia (Some factory))

    let harness = new InvoiceSyncHarness(invoiceApi, scanWindowApi, invoiceSyncApi, calendar, mainConnectionString, logged)

    try
        test harness
    finally
        harness.Dispose()
        mainContext.Dispose()
        googleContext.Dispose()
        thunderbirdDatabaseContext.Dispose()
        try File.Delete mainPath with _ -> ()
        try File.Delete googleDatabasePath with _ -> ()
        try File.Delete thunderbirdDatabasePath with _ -> ()

/// The same, over a Google account with NO default calendar chosen - Q2.11's not-ready state.
let withNotReadyInvoiceSyncHarness (test: InvoiceSyncHarness -> unit) =
    let mainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let googleDatabasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let thunderbirdDatabasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let mainConnectionString = $"Data Source={mainPath};Pooling=False"
    MigrationSetup.setupMigrations mainConnectionString
    let mainContext = DatabaseContextSetup.createDatabaseContext mainPath
    let googleContext = GoogleDatabaseContextModule.getDatabaseContext googleDatabasePath "direct"

    let thunderbirdDatabaseContext =
        ThunderbirdDatabaseContextModule.getDatabaseContext thunderbirdDatabasePath "direct"

    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add
    let calendar = FakeGoogleCalendar()
    let factory = new StubHttpClientFactory(new RespondingHandler(calendar.Respond)) :> Google.Apis.Http.IHttpClientFactory

    let invoiceApi = InvoiceApiFactory.createInvoiceApi handleError clock mainContext thunderbirdDatabaseContext
    let scanWindowApi = ScanWindowApiFactory.createScanWindowApi handleError mainContext

    let invoiceSyncApi =
        InvoiceSyncApiFactory.createInvoiceSyncApiWith
            handleError
            clock
            mainContext
            googleContext
            (GoogleCalendarClient.listEventsVia (Some factory))
            (GoogleCalendarClient.createEventVia (Some factory))
            (GoogleCalendarClient.updateEventVia (Some factory))
            (GoogleCalendarClient.deleteEventVia (Some factory))

    let harness = new InvoiceSyncHarness(invoiceApi, scanWindowApi, invoiceSyncApi, calendar, mainConnectionString, logged)

    try
        test harness
    finally
        harness.Dispose()
        mainContext.Dispose()
        googleContext.Dispose()
        thunderbirdDatabaseContext.Dispose()
        try File.Delete mainPath with _ -> ()
        try File.Delete googleDatabasePath with _ -> ()
        try File.Delete thunderbirdDatabasePath with _ -> ()
