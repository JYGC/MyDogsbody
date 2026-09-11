module MyDogsbody.Tests.Integrations.Google.GoogleAuthorizationTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open Google.Apis.Auth.OAuth2
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.Util.Store
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google

/// What Google's token endpoint reports as granted when the user grants everything asked for -
/// the space-delimited `scope` field of the code exchange's response.
let private everyScopeGranted = String.Join(" ", GoogleAuthorization.scopes)

/// What Google's granular consent screen hands back when the user leaves the Calendar box
/// unticked: the sign-in scopes only. Google shows per-scope checkboxes when a request carries a
/// sign-in scope plus a non-sign-in scope - which is exactly what `GoogleAuthorization.scopes` is.
let private signInScopesOnly = "https://www.googleapis.com/auth/userinfo.email openid"

/// A real `UserCredential` whose token carries the given granted scopes - `authoriseWith` reads
/// them to decide whether the consent is usable. Nothing reaches the network: the flow object is
/// only ever asked for a token refresh, and nothing here asks for one.
let private credentialGranting (scope: string) : UserCredential =
    let flow =
        new Flows.GoogleAuthorizationCodeFlow(
            Flows.GoogleAuthorizationCodeFlow.Initializer(
                ClientSecrets = ClientSecrets(ClientId = "client-id", ClientSecret = "client-secret"),
                Scopes = GoogleAuthorization.scopes
            )
        )

    UserCredential(flow, "account-1", TokenResponse(AccessToken = "at", RefreshToken = "rt", Scope = scope))

/// The credential a consent that granted everything returns. `fetchAccountEmail` is faked too, so
/// the only thing `authoriseWith` reads off it is the granted scope.
let private fakeCredential : UserCredential = credentialGranting everyScopeGranted

/// No collection is ever touched in these tests: every fake `runConsentFlow` either returns
/// before `GoogleCredentialDataStore` would be used, or (for the success path) never calls
/// `dataStore.StoreAsync` itself - that persistence path is covered by
/// `GoogleCredentialDataStoreTests`. A getter that throws makes that assertable.
let private unreachableCollection () : Database.Types.GoogleCredentialsCollection =
    failwith "the credential collection must not be reached by these tests"

/// What an async method hands back when it fails: a faulted `Task`, not a synchronous throw.
/// `GoogleWebAuthorizationBroker.AuthorizeAsync` is an async method, so this is the shape every
/// real consent failure arrives in. A fake that `raise`s synchronously never produces it - which
/// is how every typed catch in `authoriseWith` passed its test while being unreachable in
/// production, where the exception came through wrapped in an `AggregateException`.
let private faultedConsent (ex: exn) : string -> string -> IDataStore -> Task<UserCredential> =
    fun _ _ _ -> Task.FromException<UserCredential> ex

/// A consent flow that does what the real one does before it returns: exchanges the code and
/// persists the token through the `IDataStore` it was handed (`AuthorizationCodeFlow`'s
/// `ExchangeCodeForTokenAsync` stores it before `AuthorizeAsync` completes).
let private consentThatPersistsAToken : string -> string -> IDataStore -> Task<UserCredential> =
    fun _ accountId dataStore ->
        task {
            do! dataStore.StoreAsync(accountId, TokenResponse(AccessToken = "at", RefreshToken = "rt"))
            return fakeCredential
        }

let private withCredentialStore (test: Database.Types.GoogleDatabaseContext -> unit) =
    let databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{System.Guid.NewGuid()}.db")
    let context = Database.GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test context
    finally
        context.Dispose()
        try System.IO.File.Delete databasePath with _ -> ()

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
        faultedConsent (TokenResponseException(TokenErrorResponse(Error = "access_denied")))

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
        faultedConsent (Newtonsoft.Json.JsonException "not valid json")

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
        faultedConsent (HttpListenerException(183, "Address already in use"))

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
        // The broker's CancellationTokenSource firing leaves the task Canceled, not Faulted.
        fun _ _ _ -> Task.FromCanceled<UserCredential>(CancellationToken(true))

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
        faultedConsent (InvalidOperationException "something else went wrong")

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

[<Theory; Trait("Level", "Unit")>]
[<InlineData("not json at all")>]
[<InlineData("""{ "hello": "world" }""")>]
[<InlineData("""{ "type": "service_account", "project_id": "p", "client_email": "x@p.iam.gserviceaccount.com" }""")>]
[<InlineData("")>]
let ``authoriseWith over the REAL consent flow reports a malformed client secret as malformed, unlogged``
    (clientSecretJson: string)
    =
    // The production `runRealConsentFlow`, not a fake. It parses the secret before any browser or
    // loopback listener exists, so every input here fails at that first step and nothing opens -
    // never add a well-formed secret to this theory. requirements.md: "WHEN the stored client
    // secret is malformed THE SYSTEM SHALL report that rather than producing an obscure
    // authorisation failure" - and a pasted service-account key, or an empty paste, is as
    // malformed as a syntax error; `loadCredential` already names all of them.
    let logged = ResizeArray<MyDogsbodyException>()
    let fetchAccountEmail: UserCredential -> Task<string> = fun _ -> failwith "must not be called"

    match
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder logged.Add)
            unreachableCollection
            GoogleAuthorization.runRealConsentFlow
            fetchAccountEmail
            clientSecretJson
            "account-1"
            ()
    with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
        Assert.Equal("The stored Google client secret is malformed.", ex.Message)
        Assert.NotNull ex.InnerException
        Assert.Empty logged
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``authoriseWith does not report a failed code exchange as a cancelled consent, and logs it`` () =
    // The user DID consent; exchanging the code then failed (a client secret whose client_secret
    // value is wrong, or rotated). That arrives as the same TokenResponseException type a denial
    // does, told apart only by its OAuth error code - "access_denied" is the one that means the
    // user said no. Anything else is a failure worth a log entry, not a choice the user made.
    let logged = ResizeArray<MyDogsbodyException>()

    let runConsentFlow =
        faultedConsent (TokenResponseException(TokenErrorResponse(Error = "invalid_client", ErrorDescription = "Unauthorized")))

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
        let inner = Assert.IsType<TokenResponseException>(ex.InnerException)
        Assert.Equal("invalid_client", inner.Error.Error)
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``authoriseNewAccountWith hands back the token consent wrote when the email then cannot be read`` () =
    // Consent has already persisted a token under the freshly minted id by the time the email is
    // read. An Error from here never carries that id, so RegisterGoogleAccountWorkflow's
    // DiscardAuthorisation cannot reach it - nothing but this adapter ever can.
    withCredentialStore (fun context ->
        let logged = ResizeArray<MyDogsbodyException>()

        match
            GoogleAuthorization.authoriseNewAccountWith
                (HandleErrorBuilder logged.Add)
                context.GetCredentialCollection
                consentThatPersistsAToken
                (fun _ -> Task.FromResult "")
                "{}"
                "minted-id"
                ()
        with
        | Error ex ->
            Assert.Equal("The authorised account's email address could not be read.", ex.Message)
            Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
            Assert.Empty logged
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")

        Assert.Equal(0, context.GetCredentialCollection().Count()))

[<Fact; Trait("Level", "Integration")>]
let ``authoriseNewAccountWith hands back the token consent wrote when reading the email fails outright`` () =
    withCredentialStore (fun context ->
        let logged = ResizeArray<MyDogsbodyException>()

        let fetchAccountEmail: UserCredential -> Task<string> =
            fun _ -> Task.FromException<string>(HttpRequestException "503 Service Unavailable")

        match
            GoogleAuthorization.authoriseNewAccountWith
                (HandleErrorBuilder logged.Add)
                context.GetCredentialCollection
                consentThatPersistsAToken
                fetchAccountEmail
                "{}"
                "minted-id"
                ()
        with
        | Error ex ->
            Assert.Equal("Authorisation failed.", ex.Message)
            Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
            Assert.IsType<HttpRequestException>(ex.InnerException) |> ignore
            // The failure itself, once - the cleanup that followed it succeeded and logs nothing.
            Assert.Single logged |> ignore
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")

        Assert.Equal(0, context.GetCredentialCollection().Count()))

[<Fact; Trait("Level", "Integration")>]
let ``authoriseNewAccountWith keeps the token when the authorisation succeeds`` () =
    withCredentialStore (fun context ->
        let actual =
            GoogleAuthorization.authoriseNewAccountWith
                (HandleErrorBuilder(fun _ -> ()))
                context.GetCredentialCollection
                consentThatPersistsAToken
                (fun _ -> Task.FromResult "person@gmail.com")
                "{}"
                "minted-id"
                ()
            |> okOrFail "authoriseNewAccountWith"

        Assert.Equal(("person@gmail.com", "minted-id"), actual)

        // The registration goes on to use this token; handing it back here would leave the
        // account it is about to become with no credential at all.
        let stored =
            GoogleAuthorization.loadCredential (HandleErrorBuilder(fun _ -> ())) context.GetCredentialCollection sampleClientSecretJson "minted-id"
            |> okOrFail "loadCredential"

        Assert.Equal("at", stored.Token.AccessToken))

[<Fact; Trait("Level", "Integration")>]
let ``authoriseNewAccountWith reports a failure before consent wrote anything exactly as authoriseWith does`` () =
    // Nothing was written, so the hand-back finds nothing - and must neither log nor change the
    // answer: a denied consent stays "cancelled or denied", unlogged.
    withCredentialStore (fun context ->
        let logged = ResizeArray<MyDogsbodyException>()

        match
            GoogleAuthorization.authoriseNewAccountWith
                (HandleErrorBuilder logged.Add)
                context.GetCredentialCollection
                (faultedConsent (TokenResponseException(TokenErrorResponse(Error = "access_denied"))))
                (fun _ -> failwith "must not be called")
                "{}"
                "minted-id"
                ()
        with
        | Error ex ->
            Assert.Equal("The consent flow was cancelled or denied.", ex.Message)
            Assert.Empty logged
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")

        Assert.Equal(0, context.GetCredentialCollection().Count()))

/// A consent flow that completes, persisting its token the way the real one does, but grants only
/// the scopes it is given.
let private consentGranting (scope: string) : string -> string -> IDataStore -> Task<UserCredential> =
    fun _ accountId dataStore ->
        task {
            do! dataStore.StoreAsync(accountId, TokenResponse(AccessToken = "at", RefreshToken = "rt", Scope = scope))
            return credentialGranting scope
        }

let private calendarAccessNotGranted =
    "Google Calendar access was not granted - tick the calendar permission on Google's consent screen and try again."

[<Theory; Trait("Level", "Unit")>]
[<InlineData("https://www.googleapis.com/auth/userinfo.email openid")>]
[<InlineData("https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/userinfo.email")>]
let ``authoriseWith refuses a consent that did not grant Calendar access, unlogged, without reading the email``
    (grantedScope: string)
    =
    // Google's granular consent screen gives each non-sign-in scope its own checkbox, and "users may
    // not grant all scopes your app requests" - Google's own guide says an app must check which were
    // granted. Leaving Calendar unticked still completes consent, so before this check the account
    // was registered with a token that can never list a calendar, and every later fetch told the user
    // to re-authorise an account the page offers no way to re-authorise. The second case is a
    // near-miss by prefix: only the exact calendar scope counts, not one that merely starts with it.
    let logged = ResizeArray<MyDogsbodyException>()
    let emailReads = ResizeArray<UserCredential>()

    let runConsentFlow: string -> string -> IDataStore -> Task<UserCredential> =
        fun _ _ _ -> Task.FromResult(credentialGranting grantedScope)

    let fetchAccountEmail: UserCredential -> Task<string> =
        fun credential ->
            emailReads.Add credential
            Task.FromResult "person@gmail.com"

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
        Assert.Equal(calendarAccessNotGranted, ex.Message)
        let inner = Assert.IsType<ApplicationException>(ex.InnerException)
        Assert.Equal($"Granted scopes: {grantedScope}", inner.Message)
        Assert.Empty logged
        Assert.Empty emailReads
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("https://www.googleapis.com/auth/calendar https://www.googleapis.com/auth/userinfo.email")>]
[<InlineData("openid https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/calendar")>]
[<InlineData("")>]
[<InlineData(null)>]
let ``authoriseWith accepts a consent that granted Calendar access, or whose token does not say`` (grantedScope: string) =
    // Order is Google's business. A token with no `scope` at all is let through rather than refused:
    // Google's code exchange always reports it, so silence is not evidence of a refusal, and refusing
    // on it would refuse every registration were it ever missing - the calendar fetch still reports
    // an insufficient scope as needing re-authorisation.
    let actual =
        GoogleAuthorization.authoriseWith
            (HandleErrorBuilder(fun _ -> ()))
            unreachableCollection
            (fun _ _ _ -> Task.FromResult(credentialGranting grantedScope))
            (fun _ -> Task.FromResult "person@gmail.com")
            "{}"
            "account-1"
            ()
        |> okOrFail "authoriseWith"

    Assert.Equal(("person@gmail.com", "account-1"), actual)

[<Fact; Trait("Level", "Integration")>]
let ``authoriseNewAccountWith hands back the token consent wrote when Calendar access was not granted`` () =
    // The refusal happens after consent persisted a token under the minted id, so it is one more
    // post-consent failure only this adapter can clean up.
    withCredentialStore (fun context ->
        let logged = ResizeArray<MyDogsbodyException>()

        match
            GoogleAuthorization.authoriseNewAccountWith
                (HandleErrorBuilder logged.Add)
                context.GetCredentialCollection
                (consentGranting signInScopesOnly)
                (fun _ -> failwith "must not be called")
                "{}"
                "minted-id"
                ()
        with
        | Error ex ->
            Assert.Equal(calendarAccessNotGranted, ex.Message)
            Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise, ex.ActionName)
            Assert.Empty logged
        | Ok _ -> Assert.Fail("Expected Error, but got Ok")

        Assert.Equal(0, context.GetCredentialCollection().Count()))
