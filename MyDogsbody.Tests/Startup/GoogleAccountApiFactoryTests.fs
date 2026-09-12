module MyDogsbody.Tests.Startup.GoogleAccountApiFactoryTests

open System
open System.IO
open Xunit
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.Util.Store
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Integrations.Google.Database.Models
open MyDogsbody.Integrations.Google.Database.Types
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private handleError = HandleErrorBuilder(fun _ -> ())

/// A minimally well-formed client secret - enough for `GoogleClientSecrets.FromStream` to
/// parse without touching the network. Every test in this file that reaches a real Google.Apis
/// call is designed to fail before any HTTP request goes out (an unregistered account has no
/// stored token, so `loadCredential` refuses before `CalendarService` is ever constructed).
let private sampleClientSecret =
    """{ "installed": { "client_id": "test-client-id", "client_secret": "test-client-secret" } }"""

/// Fresh temp LiteDB file per test, context disposed and the file deleted - no test reaches
/// Startup.Startup.
///
/// `adjust` lets a test hand the factory a context whose collection getter fails, which is the
/// only way to simulate "the store is unreachable" without a hand-faked ILiteCollection. The
/// test still gets the *real* context back, so it can inspect what actually landed on disk.
let private withApiOver
    (adjust: GoogleDatabaseContext -> GoogleDatabaseContext)
    (test: GoogleDatabaseContext -> GoogleAccountApi -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    let api = GoogleAccountApiFactory.createGoogleAccountApi handleError (adjust context)

    try
        test context api
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private withApi (test: GoogleAccountApi -> unit) = withApiOver id (fun _ api -> test api)

/// Writes an account row as the store holds one.
let private storeAccountRow
    (context: GoogleDatabaseContext)
    (accountId: string)
    (emailAddress: string)
    (defaultInvoiceCalendarId: string)
    (needsReauthorisation: bool)
    =
    context
        .GetAccountCollection()
        .Upsert(
            GoogleAccountEntity(
                Id = ObjectId accountId,
                EmailAddress = emailAddress,
                DefaultInvoiceCalendarId = defaultInvoiceCalendarId,
                NeedsReauthorisation = needsReauthorisation
            )
        )
    |> ignore

let private credentialDataStore (context: GoogleDatabaseContext) =
    GoogleCredentialDataStore.GoogleCredentialDataStore(handleError, context.GetCredentialCollection) :> IDataStore

/// Stores an OAuth token for the account id, through the data store the consent flow writes to.
let private storeTokenFor (context: GoogleDatabaseContext) (accountId: string) =
    (credentialDataStore context).StoreAsync(accountId, TokenResponse(AccessToken = "at", RefreshToken = "rt"))
    |> Async.AwaitTask
    |> Async.RunSynchronously

/// Whether a token is stored for the account id, read back through that same data store.
let private hasStoredToken (context: GoogleDatabaseContext) (accountId: string) =
    (credentialDataStore context).GetAsync<TokenResponse>(accountId)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> isNull
    |> not

/// Writes an account row and a stored OAuth token for it, exactly as a completed registration
/// would leave them - the pair `RemoveAccount` is supposed to delete together.
let private registerByHand (context: GoogleDatabaseContext) (accountId: string) (emailAddress: string) =
    storeAccountRow context accountId emailAddress null false
    storeTokenFor context accountId

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

let private errorOrFail label result =
    match result with
    | Error (ex: MyDogsbodyException) -> ex
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

[<Fact; Trait("Level", "Integration")>]
let ``GetClientSecret reports None for a fresh database`` () =
    withApi (fun api -> Assert.Equal(None, api.GetClientSecret() |> okOrFail "GetClientSecret"))

[<Fact; Trait("Level", "Integration")>]
let ``SetClientSecret then GetClientSecret returns the stored value`` () =
    withApi (fun api ->
        api.SetClientSecret sampleClientSecret |> okOrFail "SetClientSecret"
        Assert.Equal(Some sampleClientSecret, api.GetClientSecret() |> okOrFail "GetClientSecret")
    )

[<Fact; Trait("Level", "Integration")>]
let ``SetClientSecret refuses a blank secret without writing anything, as an unlogged exception`` () =
    // A blank save supplies nothing. Stored, it read back as Some "" - the page then claimed a secret
    // was supplied, enabled "Add account", and registering failed at the authorisation call as
    // "malformed", which requirements.md's "say so and disable account registration" rules out.
    withApiOver id (fun context api ->
        let ex = api.SetClientSecret " \r\n " |> errorOrFail "SetClientSecret"

        Assert.Equal("Google client secret must not be empty.", ex.Message)
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.setClientSecret, ex.ActionName)
        let inner = Assert.IsType<ApplicationException>(ex.InnerException)
        Assert.Equal("Google client secret must not be empty.", inner.Message)
        Assert.Equal(0, context.GetClientSecretCollection().Count())
        Assert.Equal(None, api.GetClientSecret() |> okOrFail "GetClientSecret")
    )

[<Fact; Trait("Level", "Integration")>]
let ``GetAccounts returns an empty list for a fresh database`` () =
    withApi (fun api -> Assert.Empty(api.GetAccounts() |> okOrFail "GetAccounts"))

[<Fact; Trait("Level", "Integration")>]
let ``RegisterAccount refuses with an unlogged exception when no client secret has been supplied`` () =
    withApi (fun api ->
        let ex = api.RegisterAccount() |> errorOrFail "RegisterAccount"

        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.registerAccount, ex.ActionName)

        // The browser must never open for a registration that cannot succeed - so nothing was
        // ever saved.
        Assert.Empty(api.GetAccounts() |> okOrFail "GetAccounts")
    )

[<Fact; Trait("Level", "Integration")>]
let ``ReauthoriseAccount refuses an unregistered account as an unlogged exception, without reaching Google`` () =
    withApi (fun api ->
        api.SetClientSecret sampleClientSecret |> okOrFail "SetClientSecret"

        let ex = api.ReauthoriseAccount "never-registered" |> errorOrFail "ReauthoriseAccount"

        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.reauthoriseAccount, ex.ActionName)
    )

[<Fact; Trait("Level", "Integration")>]
let ``RemoveAccount reports an unregistered account as an unlogged exception`` () =
    withApi (fun api ->
        // A well-formed but never-stored id - GoogleAccountStore.removeOne addresses the row by
        // ObjectId, so an arbitrary string (unlike the other members, which never parse the id
        // this way) would report a store failure instead of "not registered".
        let ex = api.RemoveAccount "507f1f77bcf86cd799439011" |> errorOrFail "RemoveAccount"

        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.removeAccount, ex.ActionName)
    )

[<Fact; Trait("Level", "Integration")>]
let ``GetCalendarsFor an unregistered account reports NotAuthorised, without reaching Google`` () =
    withApi (fun api ->
        api.SetClientSecret sampleClientSecret |> okOrFail "SetClientSecret"

        // No token was ever stored for this account, so `loadCredential` refuses before
        // `CalendarService` is ever constructed - this genuinely never reaches the network.
        let ex = api.GetCalendarsFor "never-authorised" |> errorOrFail "GetCalendarsFor"

        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor, ex.ActionName)
    )

[<Fact; Trait("Level", "Integration")>]
let ``GetCalendarsFor reports ClientSecretMissing when no secret has been supplied`` () =
    withApi (fun api ->
        let ex = api.GetCalendarsFor "any-account" |> errorOrFail "GetCalendarsFor"

        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor, ex.ActionName)
    )

[<Fact; Trait("Level", "Integration")>]
let ``SetDefaultInvoiceCalendar refuses an unregistered account as an unlogged exception, without reaching Google`` () =
    withApi (fun api ->
        let ex = api.SetDefaultInvoiceCalendar "never-registered" "cal-1" |> errorOrFail "SetDefaultInvoiceCalendar"

        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.setDefaultInvoiceCalendar, ex.ActionName)
    )

[<Fact; Trait("Level", "Integration")>]
let ``RemoveAccount deletes the account's stored token as well as its row`` () =
    withApiOver id (fun context api ->
        registerByHand context "507f1f77bcf86cd799439011" "person@gmail.com"
        registerByHand context "507f1f77bcf86cd799439012" "other@gmail.com"

        api.RemoveAccount "507f1f77bcf86cd799439011" |> okOrFail "RemoveAccount"

        // requirements.md: removal "SHALL delete its local token and its record". Nothing
        // exercised the token half of that before - the factory tests only reached RemoveAccount's
        // error paths, and the E2E harness rebuilt RemoveAccount without the token deletion.
        Assert.Equal(1, context.GetAccountCollection().Count())
        Assert.Equal(1, context.GetCredentialCollection().Count())

        let remaining = api.GetAccounts() |> okOrFail "GetAccounts"
        Assert.Equal("other@gmail.com", (Assert.Single remaining).EmailAddress)
    )

[<Fact; Trait("Level", "Integration")>]
let ``RemoveAccount reports an unreachable credential store as an Error, rather than raising`` () =
    let brokenCredentials (context: GoogleDatabaseContext) =
        { context with
            GetCredentialCollection = fun () -> failwith "the credential collection is unreachable" }

    withApiOver brokenCredentials (fun context api ->
        registerByHand context "507f1f77bcf86cd799439011" "person@gmail.com"

        // `RemoveAccount` is declared `string -> Result<unit, MyDogsbodyException>`. It used to
        // raise straight through that declaration when the token deletion failed, so the UI's
        // Error branch never ran: no MudAlert, no reload, and the row it had just deleted still
        // on screen. The removal itself still succeeds - the account row really is gone, and
        // reporting a failure would tell the user to retry something already done.
        match api.RemoveAccount "507f1f77bcf86cd799439011" with
        | Ok () -> Assert.Equal(0, context.GetAccountCollection().Count())
        | Error ex -> Assert.Fail($"RemoveAccount expected Ok, but got Error: {ex.Message}")
    )

// ---------- The members that go through Google, handed a fake in Google's place.
//
// `createGoogleAccountApi` hands `registerAccountWith` and the other three the real consent flow and
// calendar client, which no test may reach, so the tests above stop at the refusals that come before
// Google. These hand them fakes instead: everything after Google - which storage binding each
// workflow is given, and how its answer and its errors are translated - runs as production runs it.
// Before these tests, a RegisterAccount handed a discard that did nothing passed the whole suite
// (PR review series 2 rounds 8 and 9). ----------

/// A fresh temp Google.db, and a handleError that records what it was asked to log.
let private withRecordingContext
    (test: GoogleDatabaseContext -> HandleErrorBuilder -> ResizeArray<MyDogsbodyException> -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    let logged = ResizeArray<MyDogsbodyException>()

    try
        test context (HandleErrorBuilder logged.Add) logged
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private setupOrFail label (result: Result<'T, CalendarError>) =
    match result with
    | Ok value -> value
    | Error error -> failwith $"{label} (test setup) failed: %A{error}"

let private accountIdOf value = GoogleAccountId.create value |> valueOrFail

let private emailOf value = GoogleEmail.create value |> valueOrFail

let private aCalendar id name isPrimary : AvailableCalendar =
    { Id = CalendarId.create id |> valueOrFail; Name = CalendarName.create name |> valueOrFail; IsPrimary = isPrimary }

let private uiCalendar id name isPrimary : CalendarUiType = { Id = id; Name = name; IsPrimary = isPrimary }

/// The one account row the store holds - failing if there is not exactly one.
let private storedAccount (context: GoogleDatabaseContext) =
    context.GetAccountCollection().FindAll() |> Seq.exactlyOne

[<Fact; Trait("Level", "Integration")>]
let ``registerAccountWith stores the authorised account, not ready, and keeps the token consent wrote`` () =
    withRecordingContext (fun context handleError logged ->
        GoogleAccountApiFactory.bindSaveClientSecret handleError context sampleClientSecret
        |> setupOrFail "SaveClientSecret"

        // What the real consent flow leaves behind: a token stored under the id it minted.
        let authoriseAccount: AuthoriseAccount =
            fun () ->
                storeTokenFor context "507f1f77bcf86cd799439011"
                Ok(emailOf "person@gmail.com", accountIdOf "507f1f77bcf86cd799439011")

        let account =
            GoogleAccountApiFactory.registerAccountWith handleError context authoriseAccount ()
            |> okOrFail "RegisterAccount"

        Assert.Equal("507f1f77bcf86cd799439011", account.Id)
        Assert.Equal("person@gmail.com", account.EmailAddress)
        Assert.Equal(None, account.DefaultInvoiceCalendarId)
        Assert.False(account.NeedsReauthorisation)

        let stored = storedAccount context
        Assert.Equal(ObjectId "507f1f77bcf86cd799439011", stored.Id)
        Assert.Equal("person@gmail.com", stored.EmailAddress)
        Assert.Null(stored.DefaultInvoiceCalendarId)
        Assert.False(stored.NeedsReauthorisation)

        Assert.True(hasStoredToken context "507f1f77bcf86cd799439011")
        Assert.Empty(logged)
    )

[<Fact; Trait("Level", "Integration")>]
let ``registerAccountWith refuses an account already registered, and discards the token consent wrote for it`` () =
    withRecordingContext (fun context handleError logged ->
        GoogleAccountApiFactory.bindSaveClientSecret handleError context sampleClientSecret
        |> setupOrFail "SaveClientSecret"

        registerByHand context "507f1f77bcf86cd799439011" "person@gmail.com"

        // Consent for the same account again. The flow stores a token under the new id it minted
        // before the duplicate can be known, and RegisterGoogleAccountWorkflow must hand it back.
        let authoriseAccount: AuthoriseAccount =
            fun () ->
                storeTokenFor context "507f1f77bcf86cd799439012"
                Ok(emailOf "person@gmail.com", accountIdOf "507f1f77bcf86cd799439012")

        let ex =
            GoogleAccountApiFactory.registerAccountWith handleError context authoriseAccount ()
            |> errorOrFail "RegisterAccount"

        Assert.Equal("The account 'person@gmail.com' is already registered.", ex.Message)
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.registerAccount, ex.ActionName)
        let inner = Assert.IsType<ApplicationException>(ex.InnerException)
        Assert.Equal("The account 'person@gmail.com' is already registered.", inner.Message)

        Assert.False(hasStoredToken context "507f1f77bcf86cd799439012")
        Assert.True(hasStoredToken context "507f1f77bcf86cd799439011")
        Assert.Equal(ObjectId "507f1f77bcf86cd799439011", (storedAccount context).Id)
        Assert.Empty(logged)
    )

[<Fact; Trait("Level", "Integration")>]
let ``registerAccountWith refuses before any consent flow when no client secret has been supplied`` () =
    withRecordingContext (fun context handleError logged ->
        let consentFlows = ResizeArray<string>()

        let authoriseAccount: AuthoriseAccount =
            fun () ->
                consentFlows.Add "consent"
                Error AuthorisationCancelled

        let ex =
            GoogleAccountApiFactory.registerAccountWith handleError context authoriseAccount ()
            |> errorOrFail "RegisterAccount"

        Assert.Equal("No Google client secret has been supplied yet.", ex.Message)
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.registerAccount, ex.ActionName)
        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Empty(consentFlows)
        Assert.Equal(0, context.GetAccountCollection().Count())
        Assert.Empty(logged)
    )

[<Fact; Trait("Level", "Integration")>]
let ``reauthoriseAccountWith stores the email Google now reports, keeping the default calendar`` () =
    withRecordingContext (fun context handleError logged ->
        storeAccountRow context "507f1f77bcf86cd799439011" "old@gmail.com" "cal-1" true

        let reauthorised = ResizeArray<string>()

        let reauthoriseAccount: ReauthoriseAccount =
            fun accountId ->
                reauthorised.Add(GoogleAccountId.value accountId)
                Ok(emailOf "new@gmail.com")

        let account =
            GoogleAccountApiFactory.reauthoriseAccountWith handleError context reauthoriseAccount "507f1f77bcf86cd799439011"
            |> okOrFail "ReauthoriseAccount"

        Assert.Equal("507f1f77bcf86cd799439011", account.Id)
        Assert.Equal("new@gmail.com", account.EmailAddress)
        Assert.Equal(Some "cal-1", account.DefaultInvoiceCalendarId)
        Assert.False(account.NeedsReauthorisation)
        Assert.Equal<string list>([ "507f1f77bcf86cd799439011" ], List.ofSeq reauthorised)

        let stored = storedAccount context
        Assert.Equal("new@gmail.com", stored.EmailAddress)
        Assert.Equal("cal-1", stored.DefaultInvoiceCalendarId)
        Assert.False(stored.NeedsReauthorisation)
        Assert.Empty(logged)
    )

[<Fact; Trait("Level", "Integration")>]
let ``reauthoriseAccountWith reports a cancelled consent as an unlogged exception, leaving the account as it was`` () =
    withRecordingContext (fun context handleError logged ->
        storeAccountRow context "507f1f77bcf86cd799439011" "old@gmail.com" "cal-1" true

        let reauthoriseAccount: ReauthoriseAccount = fun _ -> Error AuthorisationCancelled

        let ex =
            GoogleAccountApiFactory.reauthoriseAccountWith handleError context reauthoriseAccount "507f1f77bcf86cd799439011"
            |> errorOrFail "ReauthoriseAccount"

        Assert.Equal("The consent flow was cancelled or denied.", ex.Message)
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.reauthoriseAccount, ex.ActionName)
        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore

        let stored = storedAccount context
        Assert.Equal("old@gmail.com", stored.EmailAddress)
        Assert.Equal("cal-1", stored.DefaultInvoiceCalendarId)
        Assert.True(stored.NeedsReauthorisation)
        Assert.Empty(logged)
    )

[<Fact; Trait("Level", "Unit")>]
let ``getCalendarsForWith lists the account's calendars with every field mapped`` () =
    let askedFor = ResizeArray<string>()

    let listCalendars: ListCalendars =
        fun accountId ->
            askedFor.Add(GoogleAccountId.value accountId)
            Ok [ aCalendar "cal-1" "Invoices" false; aCalendar "cal-2" "Personal" true ]

    let calendars =
        GoogleAccountApiFactory.getCalendarsForWith listCalendars "507f1f77bcf86cd799439011"
        |> okOrFail "GetCalendarsFor"

    Assert.Equal<CalendarUiType list>([ uiCalendar "cal-1" "Invoices" false; uiCalendar "cal-2" "Personal" true ], calendars)
    Assert.Equal<string list>([ "507f1f77bcf86cd799439011" ], List.ofSeq askedFor)

[<Fact; Trait("Level", "Unit")>]
let ``getCalendarsForWith refuses a blank account id as an unlogged exception, without asking for calendars`` () =
    let askedFor = ResizeArray<string>()

    let listCalendars: ListCalendars =
        fun accountId ->
            askedFor.Add(GoogleAccountId.value accountId)
            Ok []

    let ex = GoogleAccountApiFactory.getCalendarsForWith listCalendars " " |> errorOrFail "GetCalendarsFor"

    Assert.Equal("Google account id must not be empty.", ex.Message)
    Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor, ex.ActionName)
    Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
    Assert.Empty(askedFor)

[<Fact; Trait("Level", "Unit")>]
let ``getCalendarsForWith passes a failed calendar fetch on with its own message`` () =
    let listCalendars: ListCalendars = fun _ -> Error(CalendarUnreachable "Could not reach Google Calendar.")

    let ex =
        GoogleAccountApiFactory.getCalendarsForWith listCalendars "507f1f77bcf86cd799439011"
        |> errorOrFail "GetCalendarsFor"

    Assert.Equal("Could not reach Google Calendar.", ex.Message)
    Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor, ex.ActionName)
    Assert.Null(ex.InnerException)

[<Fact; Trait("Level", "Integration")>]
let ``setDefaultInvoiceCalendarWith stores a calendar the account still has`` () =
    withRecordingContext (fun context handleError logged ->
        storeAccountRow context "507f1f77bcf86cd799439011" "person@gmail.com" null false

        let listCalendars: ListCalendars = fun _ -> Ok [ aCalendar "cal-1" "Invoices" false ]

        let account =
            GoogleAccountApiFactory.setDefaultInvoiceCalendarWith
                handleError
                context
                listCalendars
                "507f1f77bcf86cd799439011"
                "cal-1"
            |> okOrFail "SetDefaultInvoiceCalendar"

        Assert.Equal("507f1f77bcf86cd799439011", account.Id)
        Assert.Equal("person@gmail.com", account.EmailAddress)
        Assert.Equal(Some "cal-1", account.DefaultInvoiceCalendarId)
        Assert.False(account.NeedsReauthorisation)

        let stored = storedAccount context
        Assert.Equal("cal-1", stored.DefaultInvoiceCalendarId)
        Assert.Equal("person@gmail.com", stored.EmailAddress)
        Assert.False(stored.NeedsReauthorisation)
        Assert.Empty(logged)
    )

[<Fact; Trait("Level", "Integration")>]
let ``setDefaultInvoiceCalendarWith refuses a calendar the account no longer has, storing nothing`` () =
    withRecordingContext (fun context handleError logged ->
        storeAccountRow context "507f1f77bcf86cd799439011" "person@gmail.com" null false

        let listCalendars: ListCalendars = fun _ -> Ok [ aCalendar "cal-2" "Personal" true ]

        let ex =
            GoogleAccountApiFactory.setDefaultInvoiceCalendarWith
                handleError
                context
                listCalendars
                "507f1f77bcf86cd799439011"
                "cal-1"
            |> errorOrFail "SetDefaultInvoiceCalendar"

        Assert.Equal("The calendar 'cal-1' no longer exists.", ex.Message)
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.setDefaultInvoiceCalendar, ex.ActionName)
        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Null((storedAccount context).DefaultInvoiceCalendarId)
        Assert.Empty(logged)
    )
