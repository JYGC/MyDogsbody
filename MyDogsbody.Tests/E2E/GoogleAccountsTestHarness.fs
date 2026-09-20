module MyDogsbody.Tests.E2E.GoogleAccountsTestHarness

open System
open System.IO
open Bunit
open Microsoft.Extensions.DependencyInjection
open MudBlazor.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Tests.md - GoogleAccountsTestHarness.fs: GoogleAccountsHarness
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

    let loadClientSecret: LoadClientSecret = GoogleAccountApiFactory.bindLoadClientSecret handleError context
    let saveClientSecretDependency: SaveClientSecret = GoogleAccountApiFactory.bindSaveClientSecret handleError context
    let listGoogleAccounts: ListGoogleAccounts = GoogleAccountApiFactory.bindListGoogleAccounts handleError context

    let removeGoogleAccountDependency: RemoveGoogleAccount =
        GoogleAccountApiFactory.bindRemoveGoogleAccount handleError context

    let toException = GoogleAccountApiMappers.toMyDogsbodyException

    let api: GoogleAccountApi =
        {
            GetClientSecret =
                fun () ->
                    loadClientSecret ()
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.getClientSecret)
            SetClientSecret =
                fun secret ->
                    SetClientSecretWorkflow.setClientSecret saveClientSecretDependency secret
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.setClientSecret)
            GetAccounts =
                fun () ->
                    ListGoogleAccountsWorkflow.listGoogleAccounts listGoogleAccounts ()
                    |> Result.map (List.map GoogleAccountApiMappers.toGoogleAccountUiType)
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.getAccounts)
            RegisterAccount = GoogleAccountApiFactory.registerAccountWith handleError context authoriseAccount
            ReauthoriseAccount =
                fun _ -> failwith "not exercised by this harness"
            RemoveAccount =
                fun id ->
                    // Mirrors GoogleAccountApiFactory exactly, token deletion included - a harness
                    // that removed only the account row would leave the flow test unable to see
                    // whether the local token went with it.
                    RemoveGoogleAccountWorkflow.removeGoogleAccount removeGoogleAccountDependency id
                    |> Result.map (fun () ->
                        GoogleAuthorization.removeStoredToken handleError context.GetCredentialCollection id
                        |> ignore)
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.removeAccount)
            GetCalendarsFor = GoogleAccountApiFactory.getCalendarsForWith listCalendars
            SetDefaultInvoiceCalendar =
                GoogleAccountApiFactory.setDefaultInvoiceCalendarWith handleError context listCalendars
        }

    let harness = new GoogleAccountsHarness(authoriseAccount, listCalendars, logged, api)

    try
        test harness
    finally
        harness.Dispose()
        context.Dispose()
        try File.Delete databasePath with _ -> ()
