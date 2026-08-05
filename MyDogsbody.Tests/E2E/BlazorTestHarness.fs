module MyDogsbody.Tests.E2E.BlazorTestHarness

open System
open System.IO
open Bunit
open Microsoft.Extensions.DependencyInjection
open MudBlazor.Services
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

/// The bUnit harness the E2E level needs. It did not exist before this change - it is the one
/// level the previous change could not satisfy.
///
/// Fun.Blazor components are Blazor ComponentBases, so rendering them is ordinary bUnit. What
/// takes the setup is MudBlazor: it wants its own services registered and it reaches for JS
/// interop during render, which bUnit has to be told to tolerate.
type CredentialsHarness(credentialApi: CredentialApi, logged: ResizeArray<MyDogsbodyException>) =
    inherit TestContext()

    do
        base.Services.AddMudServices() |> ignore
        base.Services.AddSingleton<CredentialApi>(credentialApi) |> ignore

        // MudBlazor calls into JS for popovers, scroll listeners and key interception. None of
        // that is under test here, so the loose mode answers every call rather than throwing.
        base.JSInterop.Mode <- JSRuntimeMode.Loose

    /// Whatever handleError was asked to log during the flow. Asserting through this rather than
    /// against a real Logging.db keeps the test off the log database entirely, while still
    /// proving whether a failure was recorded.
    member _.Logged = logged

    member _.Api = credentialApi

/// Runs a flow against the real composition root over a real temp LiteDB file: component -> API
/// record -> workflow -> adapter -> file -> back into what the component renders.
let withCredentialsHarness (test: CredentialsHarness -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"
    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add
    let api = CredentialApiFactory.createCredentialApi handleError context.GetCredentialCollection
    let harness = new CredentialsHarness(api, logged)

    try
        test harness
    finally
        harness.Dispose()
        context.Dispose()
        try File.Delete databasePath with _ -> ()

/// The same harness over an API that cannot reach its store, for the failure flows.
let withUnreachableStoreHarness (test: CredentialsHarness -> unit) =
    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add

    let failingGetter () : MyDogsbody.Integrations.Credentials.Database.Types.CredentialsCollection =
        raise (InvalidOperationException "database is gone")

    let api = CredentialApiFactory.createCredentialApi handleError failingGetter
    let harness = new CredentialsHarness(api, logged)

    try
        test harness
    finally
        harness.Dispose()
