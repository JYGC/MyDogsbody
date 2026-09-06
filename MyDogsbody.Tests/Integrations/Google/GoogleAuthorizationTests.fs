module MyDogsbody.Tests.Integrations.Google.GoogleAuthorizationTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open Google.Apis.Auth.OAuth2
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.Util.Store
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google

/// The fake consent flow never needs a real credential - `fetchAccountEmail` is faked too, and
/// nothing in `authoriseWith` inspects the credential value itself. A null `UserCredential`
/// keeps these tests from having to construct one of Google.Apis's own flow objects.
let private fakeCredential : UserCredential = null

/// No collection is ever touched in these tests: every fake `runConsentFlow` either returns
/// before `GoogleCredentialDataStore` would be used, or (for the success path) never calls
/// `dataStore.StoreAsync` itself - that persistence path is covered by
/// `GoogleCredentialDataStoreTests`. A getter that throws makes that assertable.
let private unreachableCollection () : Database.Types.GoogleCredentialsCollection =
    failwith "the credential collection must not be reached by these tests"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

/// A minimally well-formed client secret JSON - `loadCredential`'s tests exercise the real
/// `GoogleClientSecrets.FromStream` parse, unlike `authoriseWith`'s tests where the consent flow
/// itself is faked and never touches the client secret's contents.
let private sampleClientSecretJson =
    """{ "installed": { "client_id": "test-client-id", "client_secret": "test-client-secret" } }"""

[<Fact; Trait("Level", "Unit")>]
let ``authoriseWith returns the email and the account id it was given`` () =
    let receivedAccountIds = ResizeArray<string>()

    let runConsentFlow: string -> string -> IDataStore -> Task<UserCredential> =
        fun _ accountId _ ->
            receivedAccountIds.Add accountId
            Task.FromResult fakeCredential

    let fetchAccountEmail: UserCredential -> Task<string> = fun _ -> Task.FromResult "person@gmail.com"

    let actual =
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder(fun _ -> ()))
            unreachableCollection
            runConsentFlow
            fetchAccountEmail
            "{}"
            "account-1"
            ()
        |> okOrFail "authoriseWith"

    Assert.Equal(("person@gmail.com", "account-1"), actual)
    Assert.Equal<string list>([ "account-1" ], List.ofSeq receivedAccountIds)

[<Fact; Trait("Level", "Unit")>]
let ``authoriseWith reports a cancelled or denied consent flow, unlogged`` () =
    let logged = ResizeArray<MyDogsbodyException>()

    let runConsentFlow: string -> string -> IDataStore -> Task<UserCredential> =
        fun _ _ _ -> raise (TokenResponseException(TokenErrorResponse(Error = "access_denied")))

    let fetchAccountEmail: UserCredential -> Task<string> = fun _ -> failwith "must not be called"

    match
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder logged.Add)
            unreachableCollection
            runConsentFlow
            fetchAccountEmail
            "{}"
            "account-1"
            ()
    with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
        Assert.Equal("The consent flow was cancelled or denied.", ex.Message)
        Assert.IsType<TokenResponseException>(ex.InnerException) |> ignore
        Assert.Empty logged
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``authoriseWith reports a malformed client secret, unlogged`` () =
    let logged = ResizeArray<MyDogsbodyException>()

    let runConsentFlow: string -> string -> IDataStore -> Task<UserCredential> =
        fun _ _ _ -> raise (Newtonsoft.Json.JsonException "not valid json")

    let fetchAccountEmail: UserCredential -> Task<string> = fun _ -> failwith "must not be called"

    match
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder logged.Add)
            unreachableCollection
            runConsentFlow
            fetchAccountEmail
            "not json"
            "account-1"
            ()
    with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
        Assert.Equal("The stored Google client secret is malformed.", ex.Message)
        Assert.Empty logged
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData(null)>]
let ``authoriseWith reports an unavailable email, unlogged`` (emailValue: string) =
    let logged = ResizeArray<MyDogsbodyException>()
    let runConsentFlow: string -> string -> IDataStore -> Task<UserCredential> = fun _ _ _ -> Task.FromResult fakeCredential
    let fetchAccountEmail: UserCredential -> Task<string> = fun _ -> Task.FromResult emailValue

    match
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder logged.Add)
            unreachableCollection
            runConsentFlow
            fetchAccountEmail
            "{}"
            "account-1"
            ()
    with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
        Assert.Equal("The authorised account's email address could not be read.", ex.Message)
        Assert.Empty logged
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``authoriseWith reports the loopback port already being in use, logged`` () =
    let logged = ResizeArray<MyDogsbodyException>()

    let runConsentFlow: string -> string -> IDataStore -> Task<UserCredential> =
        fun _ _ _ -> raise (HttpListenerException(183, "Address already in use"))

    let fetchAccountEmail: UserCredential -> Task<string> = fun _ -> failwith "must not be called"

    match
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder logged.Add)
            unreachableCollection
            runConsentFlow
            fetchAccountEmail
            "{}"
            "account-1"
            ()
    with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
        Assert.Equal("The loopback port is already in use.", ex.Message)
        Assert.IsType<HttpListenerException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``authoriseWith reports a timed-out consent flow, logged`` () =
    let logged = ResizeArray<MyDogsbodyException>()

    let runConsentFlow: string -> string -> IDataStore -> Task<UserCredential> =
        fun _ _ _ -> raise (OperationCanceledException "consent flow timed out")

    let fetchAccountEmail: UserCredential -> Task<string> = fun _ -> failwith "must not be called"

    match
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder logged.Add)
            unreachableCollection
            runConsentFlow
            fetchAccountEmail
            "{}"
            "account-1"
            ()
    with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
        Assert.Equal("The consent flow timed out.", ex.Message)
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``loadCredential reports that an account has no stored credential, unlogged`` () =
    let databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{System.Guid.NewGuid()}.db")
    let context = Database.GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    let logged = ResizeArray<MyDogsbodyException>()

    try
        match GoogleAuthorization.loadCredential (HandleErrorBuilder logged.Add) context.GetCredentialCollection sampleClientSecretJson "never-authorised" with
        | Error ex ->
            Assert.Equal("No stored credential for this account.", ex.Message)
            // Its own action, not `authorise`'s - a failure loading a stored token has nothing to
            // do with the consent flow, and the exception log is where that distinction is read.
            Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.loadCredential, ex.ActionName)
            Assert.Empty logged
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")
    finally
        context.Dispose()
        try System.IO.File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``loadCredential returns a credential carrying the stored access token`` () =
    let databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{System.Guid.NewGuid()}.db")
    let context = Database.GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        let dataStore =
            GoogleCredentialDataStore.GoogleCredentialDataStore(HandleErrorBuilder(fun _ -> ()), context.GetCredentialCollection)
            :> IDataStore

        let token = TokenResponse(AccessToken = "the-access-token", RefreshToken = "the-refresh-token")
        dataStore.StoreAsync("account-1", token) |> Async.AwaitTask |> Async.RunSynchronously

        match GoogleAuthorization.loadCredential (HandleErrorBuilder(fun _ -> ())) context.GetCredentialCollection sampleClientSecretJson "account-1" with
        | Ok credential -> Assert.Equal("the-access-token", credential.Token.AccessToken)
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    finally
        context.Dispose()
        try System.IO.File.Delete databasePath with _ -> ()

[<Theory; Trait("Level", "Integration")>]
[<InlineData("not json at all")>]
[<InlineData("""{ "hello": "world" }""")>]
let ``loadCredential reports a malformed stored client secret, not a bare authorisation failure, unlogged``
    (storedSecret: string)
    =
    // requirements.md's edge case: "WHEN the stored client secret is malformed THE SYSTEM SHALL
    // report that rather than producing an obscure authorisation failure." `authoriseWith` already
    // names this failure; `loadCredential` parses the very same secret and did not, so replacing a
    // working secret with a bad paste turned every calendar picker into "Authorisation failed."
    let databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{System.Guid.NewGuid()}.db")
    let context = Database.GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    let logged = ResizeArray<MyDogsbodyException>()

    try
        let dataStore =
            GoogleCredentialDataStore.GoogleCredentialDataStore(HandleErrorBuilder(fun _ -> ()), context.GetCredentialCollection)
            :> IDataStore

        dataStore.StoreAsync("account-1", TokenResponse(AccessToken = "at", RefreshToken = "rt"))
        |> Async.AwaitTask
        |> Async.RunSynchronously

        match GoogleAuthorization.loadCredential (HandleErrorBuilder logged.Add) context.GetCredentialCollection storedSecret "account-1" with
        | Error ex ->
            Assert.Equal("The stored Google client secret is malformed.", ex.Message)
            Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.loadCredential, ex.ActionName)
            Assert.NotNull ex.InnerException
            // A secret the user pasted wrongly is a state they can fix, not a defect worth a stack
            // trace - the same unlogged treatment `authoriseWith` gives the identical failure.
            Assert.Empty logged
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")
    finally
        context.Dispose()
        try System.IO.File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Unit")>]
let ``authoriseWith reports any other failure generically, logged, with the inner exception preserved`` () =
    let logged = ResizeArray<MyDogsbodyException>()

    let runConsentFlow: string -> string -> IDataStore -> Task<UserCredential> =
        fun _ _ _ -> raise (InvalidOperationException "something else went wrong")

    let fetchAccountEmail: UserCredential -> Task<string> = fun _ -> failwith "must not be called"

    match
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder logged.Add)
            unreachableCollection
            runConsentFlow
            fetchAccountEmail
            "{}"
            "account-1"
            ()
    with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
        Assert.Equal("Authorisation failed.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``removeStoredToken deletes the account's stored token`` () =
    let databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{System.Guid.NewGuid()}.db")
    let context = Database.GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        let dataStore =
            GoogleCredentialDataStore.GoogleCredentialDataStore(HandleErrorBuilder(fun _ -> ()), context.GetCredentialCollection)
            :> IDataStore

        dataStore.StoreAsync("account-1", TokenResponse(AccessToken = "at", RefreshToken = "rt"))
        |> Async.AwaitTask
        |> Async.RunSynchronously

        dataStore.StoreAsync("account-2", TokenResponse(AccessToken = "at2", RefreshToken = "rt2"))
        |> Async.AwaitTask
        |> Async.RunSynchronously

        Assert.Equal(2, context.GetCredentialCollection().Count())

        GoogleAuthorization.removeStoredToken (HandleErrorBuilder(fun _ -> ())) context.GetCredentialCollection "account-1"
        |> okOrFail "removeStoredToken"

        // requirements.md: removing an account "SHALL delete its local token and its record" -
        // and only that account's. The other account's token is untouched.
        Assert.Equal(1, context.GetCredentialCollection().Count())

        let remaining =
            GoogleAuthorization.loadCredential (HandleErrorBuilder(fun _ -> ())) context.GetCredentialCollection sampleClientSecretJson "account-2"
            |> okOrFail "loadCredential"

        Assert.Equal("at2", remaining.Token.AccessToken)

        match GoogleAuthorization.loadCredential (HandleErrorBuilder(fun _ -> ())) context.GetCredentialCollection sampleClientSecretJson "account-1" with
        | Error ex -> Assert.Equal("No stored credential for this account.", ex.Message)
        | Ok _ -> Assert.Fail("Expected the deleted account's credential to be gone")
    finally
        context.Dispose()
        try System.IO.File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``removeStoredToken deleting a token no row carries succeeds without logging`` () =
    let databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{System.Guid.NewGuid()}.db")
    let context = Database.GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    let logged = ResizeArray<MyDogsbodyException>()

    try
        GoogleAuthorization.removeStoredToken (HandleErrorBuilder logged.Add) context.GetCredentialCollection "never-authorised"
        |> okOrFail "removeStoredToken"

        Assert.Empty logged
    finally
        context.Dispose()
        try System.IO.File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Unit")>]
let ``removeStoredToken reports an unreachable credential store as a Result, rather than raising`` () =
    // IDataStore is exception-based by construction, so GoogleCredentialDataStore re-raises what
    // GoogleCredentialStore hands back. Before this was written with handleError, that exception
    // travelled straight out of GoogleAccountApi.RemoveAccount, past its declared Result and past
    // the UI's error branch - the account row already deleted, no alert, no reload.
    let logged = ResizeArray<MyDogsbodyException>()

    match GoogleAuthorization.removeStoredToken (HandleErrorBuilder logged.Add) unreachableCollection "account-1" with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.removeStoredToken, ex.ActionName)
        Assert.Equal("Failed to delete the stored Google credential.", ex.Message)
        Assert.NotNull ex.InnerException
        Assert.NotEmpty logged
    | Ok () -> Assert.Fail("Expected Error, but got Ok")
