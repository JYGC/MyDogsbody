module MyDogsbody.Tests.E2E.GoogleAccountsTestHarness

open System
open System.IO
open Bunit
open Microsoft.Extensions.DependencyInjection
open MudBlazor.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

/// A bUnit TestContext subclass wired for MudBlazor (AddMudServices, JSRuntimeMode.Loose), over
/// the Google integration's own LiteDB database.
///
/// `RegisterAccount`/`GetCalendarsFor`/`SetDefaultInvoiceCalendar` are built here rather than via
/// `GoogleAccountApiFactory`, because the real consent flow needs a system browser and the real
/// calendar client needs a network connection - neither of which any test may require
/// (tasks.md's own header rule). Everything storage-facing (`GoogleAccountStore`, the LiteDB
/// context, the domain workflows, the error translation) is exactly what production runs; only
/// the two network-touching adapter calls are replaced by test-controlled fakes, matching the
/// same seam `GoogleAuthorizationTests`/`GoogleAccountApiFactoryTests` already exercise.
type GoogleAccountsHarness
    (
        authoriseAccount: AuthoriseAccount,
        listCalendars: ListCalendars,
        logged: ResizeArray<MyDogsbodyException>,
        api: GoogleAccountApi
    ) =
    inherit TestContext()

    do
        base.Services.AddMudServices() |> ignore
        base.Services.AddSingleton<GoogleAccountApi>(api) |> ignore
        base.JSInterop.Mode <- JSRuntimeMode.Loose

    /// Whatever handleError was asked to log during the flow. Asserting through this rather than
    /// against a real Logging.db keeps the test off the log database entirely, while still
    /// proving whether a failure was recorded.
    member _.Logged = logged

    member _.Api = api

/// `authoriseAccount` and `listCalendars` are the two network-facing fakes a test controls;
/// everything else is the real composition over a real temp LiteDB file.
let withGoogleAccountsHarness
    (authoriseAccount: AuthoriseAccount)
    (listCalendars: ListCalendars)
    (test: GoogleAccountsHarness -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add

    let loadClientSecret: LoadClientSecret =
        fun () ->
            GoogleAccountStore.loadClientSecret handleError context.GetClientSecretCollection ()
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let saveClientSecretDependency: SaveClientSecret =
        fun secret ->
            GoogleAccountStore.saveClientSecret handleError context.GetClientSecretCollection secret
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let listGoogleAccounts: ListGoogleAccounts =
        fun () ->
            GoogleAccountStore.getAll handleError context.GetAccountCollection ()
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let saveGoogleAccount: SaveGoogleAccount =
        fun account ->
            GoogleAccountStore.saveOne handleError context.GetAccountCollection account
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let removeGoogleAccountDependency: RemoveGoogleAccount =
        fun accountId ->
            GoogleAccountStore.removeOne handleError context.GetAccountCollection accountId
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let toException = GoogleAccountApiMappers.toMyDogsbodyException

    let api: GoogleAccountApi =
        {
            GetClientSecret =
                fun () ->
                    loadClientSecret ()
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.getClientSecret)
            SetClientSecret =
                fun secret ->
                    saveClientSecretDependency secret
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.setClientSecret)
            GetAccounts =
                fun () ->
                    ListGoogleAccountsWorkflow.listGoogleAccounts listGoogleAccounts ()
                    |> Result.map (List.map GoogleAccountApiMappers.toGoogleAccountUiType)
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.getAccounts)
            RegisterAccount =
                fun () ->
                    RegisterGoogleAccountWorkflow.registerGoogleAccount
                        loadClientSecret
                        listGoogleAccounts
                        authoriseAccount
                        saveGoogleAccount
                        ()
                    |> Result.map GoogleAccountApiMappers.toGoogleAccountUiType
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.registerAccount)
            ReauthoriseAccount =
                fun _ -> failwith "not exercised by this harness"
            RemoveAccount =
                fun id ->
                    RemoveGoogleAccountWorkflow.removeGoogleAccount removeGoogleAccountDependency id
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.removeAccount)
            GetCalendarsFor =
                fun id ->
                    result {
                        let! accountId = GoogleAccountId.create id |> Result.mapError GoogleAccountIdInvalid
                        let! calendars = listCalendars accountId
                        return calendars |> List.map GoogleAccountApiMappers.toCalendarUiType
                    }
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor)
            SetDefaultInvoiceCalendar =
                fun accountId calendarId ->
                    SetDefaultInvoiceCalendarWorkflow.setDefaultInvoiceCalendar
                        listGoogleAccounts
                        listCalendars
                        saveGoogleAccount
                        accountId
                        calendarId
                    |> Result.map GoogleAccountApiMappers.toGoogleAccountUiType
                    |> Result.mapError (
                        toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.setDefaultInvoiceCalendar
                    )
        }

    let harness = new GoogleAccountsHarness(authoriseAccount, listCalendars, logged, api)

    try
        test harness
    finally
        harness.Dispose()
        context.Dispose()
        try File.Delete databasePath with _ -> ()
