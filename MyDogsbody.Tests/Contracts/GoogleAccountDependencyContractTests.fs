module MyDogsbody.Tests.Contracts.GoogleAccountDependencyContractTests

open System
open System.IO
open Xunit
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.Util.Store
open MyDogsbody.Builders
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Startup

// LoadClientSecret, SaveClientSecret, ListGoogleAccounts, SaveGoogleAccount, RemoveGoogleAccount
// and DiscardAuthorisation are dependency function types a domain workflow consumes - published
// interfaces, so CLAUDE.md's shared-suite rule applies: the same suite runs against the real
// bindings (GoogleAccountApiFactory over a temp LiteDB) and an in-memory fake, so a workflow unit
// test's fake cannot drift into a shape the real store never produces.
//
// AuthoriseAccount and ListCalendars are not here - the real Google network/browser cannot run in
// an automated test. AuthoriseAccount's real-side coverage is `GoogleAuthorizationTests`
// (`authoriseWith` bound to fakes at the innermost SDK seam); ListCalendars' is
// `ListCalendarsDependencyContractTests` (the real adapter over a stubbed HttpMessageHandler).

let private handleError = HandleErrorBuilder(fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private email value = GoogleEmail.create value |> valueOrFail

type private GoogleAccountDependencies =
    {
        LoadClientSecret: LoadClientSecret
        SaveClientSecret: SaveClientSecret
        ListGoogleAccounts: ListGoogleAccounts
        SaveGoogleAccount: SaveGoogleAccount
        RemoveGoogleAccount: RemoveGoogleAccount
        DiscardAuthorisation: DiscardAuthorisation

        // Harness affordances, not dependency types: DiscardAuthorisation's whole job is to take
        // an authorisation away, so a suite that cannot put one there first, or count what is
        // left, can only assert that it returned Ok.
        StoreAuthorisationFor: GoogleAccountId -> unit
        AuthorisationCount: unit -> int
    }

// ---------- the real bindings, over a temp LiteDB file ----------

let private withRealDependencies (test: GoogleAccountDependencies -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test
            {
                LoadClientSecret =
                    fun () ->
                        GoogleAccountStore.loadClientSecret handleError context.GetClientSecretCollection ()
                        |> Result.mapError GoogleAccountApiMappers.toStoreError
                SaveClientSecret =
                    fun secret ->
                        GoogleAccountStore.saveClientSecret handleError context.GetClientSecretCollection secret
                        |> Result.mapError GoogleAccountApiMappers.toStoreError
                ListGoogleAccounts =
                    fun () ->
                        GoogleAccountStore.getAll handleError context.GetAccountCollection ()
                        |> Result.mapError GoogleAccountApiMappers.toStoreError
                SaveGoogleAccount =
                    fun account ->
                        GoogleAccountStore.saveOne handleError context.GetAccountCollection account
                        |> Result.mapError GoogleAccountApiMappers.toStoreError
                RemoveGoogleAccount =
                    fun accountId ->
                        GoogleAccountStore.removeOne handleError context.GetAccountCollection accountId
                        |> Result.mapError GoogleAccountApiMappers.toStoreError
                DiscardAuthorisation =
                    fun accountId ->
                        GoogleAuthorization.removeStoredToken
                            handleError
                            context.GetCredentialCollection
                            (GoogleAccountId.value accountId)
                        |> Result.mapError GoogleAccountApiMappers.toStoreError
                StoreAuthorisationFor =
                    fun accountId ->
                        let dataStore =
                            GoogleCredentialDataStore.GoogleCredentialDataStore(
                                handleError,
                                context.GetCredentialCollection
                            )
                            :> IDataStore

                        dataStore.StoreAsync(
                            GoogleAccountId.value accountId,
                            TokenResponse(AccessToken = "at", RefreshToken = "rt")
                        )
                        |> Async.AwaitTask
                        |> Async.RunSynchronously
                AuthorisationCount = fun () -> context.GetCredentialCollection().Count()
            }
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

// ---------- the in-memory fake ----------

let private withFakeDependencies (test: GoogleAccountDependencies -> unit) =
    let mutable clientSecret: string option = None
    let accounts = ResizeArray<RegisteredGoogleAccount>()
    let authorisations = ResizeArray<GoogleAccountId>()

    test
        {
            LoadClientSecret = fun () -> Ok clientSecret
            SaveClientSecret =
                fun secret ->
                    clientSecret <- Some secret
                    Ok()
            ListGoogleAccounts = fun () -> Ok(List.ofSeq accounts)
            SaveGoogleAccount =
                fun account ->
                    match accounts |> Seq.tryFindIndex (fun a -> a.Id = account.Id) with
                    | Some index -> accounts.[index] <- account
                    | None -> accounts.Add account

                    Ok account
            RemoveGoogleAccount =
                fun accountId ->
                    match accounts |> Seq.tryFindIndex (fun a -> a.Id = accountId) with
                    | Some index ->
                        accounts.RemoveAt index
                        Ok true
                    | None -> Ok false
            DiscardAuthorisation =
                fun accountId ->
                    authorisations.Remove accountId |> ignore
                    Ok()
            StoreAuthorisationFor = fun accountId -> authorisations.Add accountId
            AuthorisationCount = fun () -> authorisations.Count
        }

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real bindings" |]; [| box "in-memory fake" |] ]

let private withImplementation (name: string) (test: GoogleAccountDependencies -> unit) =
    match name with
    | "real bindings" -> withRealDependencies test
    | "in-memory fake" -> withFakeDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"{label} expected Ok, but got Error: {error}"

let private anAccount id emailValue : RegisteredGoogleAccount =
    {
        Id = GoogleAccountId.create id |> valueOrFail
        EmailAddress = email emailValue
        DefaultInvoiceCalendar = None
        NeedsReauthorisation = false
    }

// ---------- client secret ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``LoadClientSecret returns None before anything is saved`` (implementation: string) =
    withImplementation implementation (fun deps -> Assert.Equal(None, deps.LoadClientSecret() |> okOrFail "LoadClientSecret"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SaveClientSecret then LoadClientSecret round trips the value`` (implementation: string) =
    withImplementation implementation (fun deps ->
        deps.SaveClientSecret "the-secret" |> okOrFail "SaveClientSecret"
        Assert.Equal(Some "the-secret", deps.LoadClientSecret() |> okOrFail "LoadClientSecret")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``saving a client secret twice replaces the stored value`` (implementation: string) =
    withImplementation implementation (fun deps ->
        deps.SaveClientSecret "first" |> okOrFail "SaveClientSecret first"
        deps.SaveClientSecret "second" |> okOrFail "SaveClientSecret second"
        Assert.Equal(Some "second", deps.LoadClientSecret() |> okOrFail "LoadClientSecret")
    )

// ---------- accounts ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``ListGoogleAccounts returns an empty list for a fresh store`` (implementation: string) =
    withImplementation implementation (fun deps -> Assert.Empty(deps.ListGoogleAccounts() |> okOrFail "ListGoogleAccounts"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SaveGoogleAccount then ListGoogleAccounts returns every field intact`` (implementation: string) =
    withImplementation implementation (fun deps ->
        let saved = anAccount "507f1f77bcf86cd799439011" "person@gmail.com" |> deps.SaveGoogleAccount |> okOrFail "SaveGoogleAccount"

        let listed = Assert.Single(deps.ListGoogleAccounts() |> okOrFail "ListGoogleAccounts")
        Assert.Equal(saved.Id, listed.Id)
        Assert.Equal("person@gmail.com", GoogleEmail.value listed.EmailAddress)
        Assert.Equal(None, listed.DefaultInvoiceCalendar)
        Assert.False listed.NeedsReauthorisation
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``saving an account twice updates rather than duplicating`` (implementation: string) =
    withImplementation implementation (fun deps ->
        let original = anAccount "507f1f77bcf86cd799439011" "person@gmail.com"
        original |> deps.SaveGoogleAccount |> okOrFail "SaveGoogleAccount first" |> ignore

        { original with NeedsReauthorisation = true }
        |> deps.SaveGoogleAccount
        |> okOrFail "SaveGoogleAccount second"
        |> ignore

        let listed = Assert.Single(deps.ListGoogleAccounts() |> okOrFail "ListGoogleAccounts")
        Assert.True listed.NeedsReauthorisation
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``RemoveGoogleAccount deletes a known account and reports true`` (implementation: string) =
    withImplementation implementation (fun deps ->
        let saved =
            anAccount "507f1f77bcf86cd799439011" "person@gmail.com" |> deps.SaveGoogleAccount |> okOrFail "SaveGoogleAccount"

        Assert.True(deps.RemoveGoogleAccount saved.Id |> okOrFail "RemoveGoogleAccount")
        Assert.Empty(deps.ListGoogleAccounts() |> okOrFail "ListGoogleAccounts")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``RemoveGoogleAccount reports false for an id that carries no row`` (implementation: string) =
    withImplementation implementation (fun deps ->
        let unknownId = GoogleAccountId.create "507f1f77bcf86cd799439099" |> valueOrFail
        Assert.False(deps.RemoveGoogleAccount unknownId |> okOrFail "RemoveGoogleAccount")
    )

// ---------- discarding an authorisation ----------
//
// RegisterGoogleAccountWorkflow calls this when it refuses a duplicate: consent has already
// persisted a token against the id authoriseAccount just returned, so a refusal that only
// declines to save would strand it - no account row would point at it, and only removing an
// account deletes a token.

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DiscardAuthorisation removes the authorisation it names`` (implementation: string) =
    withImplementation implementation (fun deps ->
        let accountId = GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail
        deps.StoreAuthorisationFor accountId
        Assert.Equal(1, deps.AuthorisationCount())

        deps.DiscardAuthorisation accountId |> okOrFail "DiscardAuthorisation"

        Assert.Equal(0, deps.AuthorisationCount())
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DiscardAuthorisation leaves every other account's authorisation alone`` (implementation: string) =
    withImplementation implementation (fun deps ->
        let discarded = GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail
        let kept = GoogleAccountId.create "507f1f77bcf86cd799439012" |> valueOrFail
        deps.StoreAuthorisationFor discarded
        deps.StoreAuthorisationFor kept

        deps.DiscardAuthorisation discarded |> okOrFail "DiscardAuthorisation"

        Assert.Equal(1, deps.AuthorisationCount())
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``DiscardAuthorisation succeeds for an id that was never authorised`` (implementation: string) =
    withImplementation implementation (fun deps ->
        // Nothing to discard is not a failure - the workflow calls this on a path where the
        // authorisation may already be gone, and a reported error there would replace
        // AccountAlreadyRegistered with noise.
        let accountId = GoogleAccountId.create "507f1f77bcf86cd799439099" |> valueOrFail

        deps.DiscardAuthorisation accountId |> okOrFail "DiscardAuthorisation"

        Assert.Equal(0, deps.AuthorisationCount())
    )
