module MyDogsbody.Tests.Startup.GoogleAccountApiFactoryTests

open System
open System.IO
open Xunit
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.Util.Store
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
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

/// Writes an account row and a stored OAuth token for it, exactly as a completed registration
/// would leave them - the pair `RemoveAccount` is supposed to delete together.
let private registerByHand (context: GoogleDatabaseContext) (accountId: string) (emailAddress: string) =
    context
        .GetAccountCollection()
        .Upsert(
            GoogleAccountEntity(
                Id = ObjectId accountId,
                EmailAddress = emailAddress,
                DefaultInvoiceCalendarId = null,
                NeedsReauthorisation = false
            )
        )
    |> ignore

    let dataStore =
        GoogleCredentialDataStore.GoogleCredentialDataStore(handleError, context.GetCredentialCollection) :> IDataStore

    dataStore.StoreAsync(accountId, TokenResponse(AccessToken = "at", RefreshToken = "rt"))
    |> Async.AwaitTask
    |> Async.RunSynchronously

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
