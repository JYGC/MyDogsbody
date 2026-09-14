module MyDogsbody.Tests.Contracts.GoogleAccountApiContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// GoogleAccountApi is a record of functions, so a fake is a record literal rather than a class -
// the same shape MailAccountApiContractTests uses. This suite is scoped to the paths neither
// implementation ever needs the real Google network for: registering, re-authorising and
// fetching calendars all refuse before reaching Google whenever their precondition (a client
// secret, a registered account) is not met, and that refusal is exactly what both the real API
// and the fake must agree on.

let private handleError = HandleErrorBuilder(fun _ -> ())

let private sampleClientSecret =
    """{ "installed": { "client_id": "test-client-id", "client_secret": "test-client-secret" } }"""

// ---------- the real API, over a temp LiteDB file ----------

let private withRealApi (test: GoogleAccountApi -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test (GoogleAccountApiFactory.createGoogleAccountApi handleError context)
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

// ---------- the in-memory fake ----------

let private withFakeApi (test: GoogleAccountApi -> unit) =
    let mutable clientSecret: string option = None
    let accounts = ResizeArray<GoogleAccountUiType>()

    let fail action message = Error(MyDogsbodyException(action, message, ApplicationException message))

    test
        {
            GetClientSecret = fun () -> Ok clientSecret
            SetClientSecret =
                fun secret ->
                    if String.IsNullOrWhiteSpace secret then
                        fail ActionNames.MyDogsbody.Startup.GoogleAccountApi.setClientSecret "Google client secret must not be empty."
                    else
                        clientSecret <- Some secret
                        Ok()
            GetAccounts = fun () -> Ok(List.ofSeq accounts)
            RegisterAccount =
                fun () ->
                    match clientSecret with
                    | None -> fail ActionNames.MyDogsbody.Startup.GoogleAccountApi.registerAccount "No Google client secret has been supplied yet."
                    | Some _ -> failwith "the fake never simulates a successful registration - it needs the real Google network"
            ReauthoriseAccount =
                fun id ->
                    if accounts |> Seq.exists (fun account -> account.Id = id) then
                        failwith "the fake never simulates a successful re-authorisation - it needs the real Google network"
                    else
                        fail ActionNames.MyDogsbody.Startup.GoogleAccountApi.reauthoriseAccount $"No Google account was found with id '{id}'."
            RemoveAccount =
                fun id ->
                    match accounts |> Seq.tryFindIndex (fun account -> account.Id = id) with
                    | Some index ->
                        accounts.RemoveAt index
                        Ok()
                    | None -> fail ActionNames.MyDogsbody.Startup.GoogleAccountApi.removeAccount $"No Google account was found with id '{id}'."
            GetCalendarsFor =
                fun id ->
                    match clientSecret with
                    | None -> fail ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor "No Google client secret has been supplied yet."
                    | Some _ ->
                        if accounts |> Seq.exists (fun account -> account.Id = id) then
                            Ok []
                        else
                            fail ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor "This Google account needs to be re-authorised."
            SetDefaultInvoiceCalendar =
                fun accountId _ ->
                    if accounts |> Seq.exists (fun account -> account.Id = accountId) then
                        failwith "the fake never simulates a successful calendar choice - it needs the real Google network"
                    else
                        fail
                            ActionNames.MyDogsbody.Startup.GoogleAccountApi.setDefaultInvoiceCalendar
                            $"No Google account was found with id '{accountId}'."
        }

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real api" |]; [| box "fake api" |] ]

let private withImplementation (name: string) (test: GoogleAccountApi -> unit) =
    match name with
    | "real api" -> withRealApi test
    | "fake api" -> withFakeApi test
    | other -> failwith $"Unknown implementation '{other}'"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error(caughtException: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {caughtException.Message}"

let private errorOrFail label result =
    match result with
    | Error(caughtException: MyDogsbodyException) -> caughtException
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``GetClientSecret reports None before anything is saved`` (implementation: string) =
    withImplementation implementation (fun api -> Assert.Equal(None, api.GetClientSecret() |> okOrFail "GetClientSecret"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SetClientSecret then GetClientSecret returns the stored value`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.SetClientSecret sampleClientSecret |> okOrFail "SetClientSecret"
        Assert.Equal(Some sampleClientSecret, api.GetClientSecret() |> okOrFail "GetClientSecret")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SetClientSecret refuses a blank secret as an unlogged exception, keeping what was stored`` (implementation: string) =
    // The page reads "a secret is stored" as "one has been supplied", so a blank one must never be
    // stored - not over nothing, and not over a secret that works.
    withImplementation implementation (fun api ->
        let refusedOverNothing = api.SetClientSecret "" |> errorOrFail "SetClientSecret"
        Assert.Equal(None, api.GetClientSecret() |> okOrFail "GetClientSecret")

        api.SetClientSecret sampleClientSecret |> okOrFail "SetClientSecret"
        let refusedOverWorking = api.SetClientSecret "   " |> errorOrFail "SetClientSecret"
        Assert.Equal(Some sampleClientSecret, api.GetClientSecret() |> okOrFail "GetClientSecret")

        for caughtException in [ refusedOverNothing; refusedOverWorking ] do
            Assert.Equal("Google client secret must not be empty.", caughtException.Message)
            Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.setClientSecret, caughtException.ActionName)
            let inner = Assert.IsType<ApplicationException>(caughtException.InnerException)
            Assert.Equal("Google client secret must not be empty.", inner.Message)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``GetAccounts returns an empty list before anything is registered`` (implementation: string) =
    withImplementation implementation (fun api -> Assert.Empty(api.GetAccounts() |> okOrFail "GetAccounts"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``RegisterAccount refuses with an unlogged exception when no client secret has been supplied`` (implementation: string) =
    withImplementation implementation (fun api ->
        let caughtException = api.RegisterAccount() |> errorOrFail "RegisterAccount"
        Assert.IsType<ApplicationException>(caughtException.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.registerAccount, caughtException.ActionName)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``ReauthoriseAccount refuses an unregistered account as an unlogged exception`` (implementation: string) =
    withImplementation implementation (fun api ->
        let caughtException = api.ReauthoriseAccount "never-registered" |> errorOrFail "ReauthoriseAccount"
        Assert.IsType<ApplicationException>(caughtException.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.reauthoriseAccount, caughtException.ActionName)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``GetCalendarsFor refuses with an unlogged exception when no client secret has been supplied`` (implementation: string) =
    withImplementation implementation (fun api ->
        let caughtException = api.GetCalendarsFor "any-account" |> errorOrFail "GetCalendarsFor"
        Assert.IsType<ApplicationException>(caughtException.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor, caughtException.ActionName)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SetDefaultInvoiceCalendar refuses an unregistered account as an unlogged exception`` (implementation: string) =
    withImplementation implementation (fun api ->
        let caughtException = api.SetDefaultInvoiceCalendar "never-registered" "cal-1" |> errorOrFail "SetDefaultInvoiceCalendar"
        Assert.IsType<ApplicationException>(caughtException.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.GoogleAccountApi.setDefaultInvoiceCalendar, caughtException.ActionName)
    )
