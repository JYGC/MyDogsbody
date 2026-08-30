module MyDogsbody.Tests.E2E.MailAccountsTestHarness

open System
open System.IO
open Bunit
open Microsoft.Extensions.DependencyInjection
open MudBlazor.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

/// A bUnit TestContext subclass wired for MudBlazor (AddMudServices, JSRuntimeMode.Loose), over
/// the Thunderbird integration's own LiteDB database - plus a FolderPicker, since the page needs
/// one injected the way the WPF host supplies it in production. The folder picker is a lambda
/// here, so no window opens.
type MailAccountsHarness(mailAccountApi: MailAccountApi, folderPicker: FolderPicker, logged: ResizeArray<MyDogsbodyException>) =
    inherit TestContext()

    do
        base.Services.AddMudServices() |> ignore
        base.Services.AddSingleton<MailAccountApi>(mailAccountApi) |> ignore
        base.Services.AddSingleton<FolderPicker>(folderPicker) |> ignore
        base.JSInterop.Mode <- JSRuntimeMode.Loose

    /// Whatever handleError was asked to log during the flow. Asserting through this rather than
    /// against a real Logging.db keeps the test off the log database entirely, while still
    /// proving whether a failure was recorded.
    member _.Logged = logged

    member _.Api = mailAccountApi

/// Runs a flow against the real composition root over a real temp LiteDB file: component -> API
/// record -> workflow -> adapter -> file -> back into what the component renders.
let withMailAccountsHarness (folderPicker: FolderPicker) (test: MailAccountsHarness -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "direct"
    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add
    let api = MailAccountApiFactory.createMailAccountApi handleError context
    let harness = new MailAccountsHarness(api, folderPicker, logged)

    try
        test harness
    finally
        harness.Dispose()
        context.Dispose()

        try
            File.Delete databasePath
        with _ ->
            ()
