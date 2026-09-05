module MyDogsbody.Tests.Startup.GoogleAccountApiFactoryTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google.Database
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
let private withApi (test: GoogleAccountApi -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    let api = GoogleAccountApiFactory.createGoogleAccountApi handleError context

    try
        test api
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

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
