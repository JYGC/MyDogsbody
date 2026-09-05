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
