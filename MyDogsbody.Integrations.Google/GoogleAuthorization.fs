/// The Google integration's OAuth adapter: the system-browser + loopback consent flow
/// (`GoogleWebAuthorizationBroker`), backed by `GoogleCredentialDataStore` rather than a
/// `FileDataStore` (design decision 1), plus the one HTTP call needed to read the newly
/// authorised account's own email address (Q3.5's `userinfo.email` scope).
///
/// Outer ring shape - `handleError` first, `Result<'T, MyDogsbodyException>` out.
/// `GoogleAccountApiMappers.toCalendarError` (change #6's composition root) translates the
/// distinct `Message` strings this file chooses into the right `CalendarError` case.
module MyDogsbody.Integrations.Google.GoogleAuthorization

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open LiteDB
open Newtonsoft.Json.Linq
open Google.Apis.Auth.OAuth2
open Google.Apis.Auth.OAuth2.Flows
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.Calendar.v3
open Google.Apis.Util.Store
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google.Database.Types
open MyDogsbody.Integrations.Google.GoogleCredentialDataStore

/// The scopes requested: calendar read/write, and the account's own address (Q3.5).
let scopes: string list =
    [ CalendarService.Scope.Calendar; "https://www.googleapis.com/auth/userinfo.email" ]

/// How long a user has to complete (or abandon) the browser consent flow before it is treated
/// as timed out (requirements.md's "the user closes the browser without completing consent"
/// edge case).
let private consentTimeout = TimeSpan.FromMinutes 5.0

/// The real consent flow: parses the pasted client secret, then runs
/// `GoogleWebAuthorizationBroker`'s system-browser + loopback dance, storing the resulting
/// token through whichever `IDataStore` it is given - `GoogleCredentialDataStore` in production.
let runRealConsentFlow (clientSecretJson: string) (accountId: string) (dataStore: IDataStore) : Task<UserCredential> =
    task {
        use stream = new MemoryStream(Encoding.UTF8.GetBytes clientSecretJson)
        let secrets = GoogleClientSecrets.FromStream(stream).Secrets
        use cts = new CancellationTokenSource(consentTimeout)
        return! GoogleWebAuthorizationBroker.AuthorizeAsync(secrets, scopes, accountId, cts.Token, dataStore)
    }

/// Reads the newly authorised account's own email via Google's userinfo REST endpoint, using
/// the credential's access token as a bearer token.
///
/// Not covered by an automated test - task 10.4's manual verification against a real account is
/// where this line actually gets exercised. `authoriseWith`'s tests substitute a fake in its
/// place, per tasks.md's "no test may require network" rule.
let fetchRealAccountEmail (credential: UserCredential) : Task<string> =
    task {
        use httpClient = new HttpClient()
        use request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo")
        request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", credential.Token.AccessToken)

        use! response = httpClient.SendAsync request
        response.EnsureSuccessStatusCode() |> ignore

        let! body = response.Content.ReadAsStringAsync()
        let email = (JObject.Parse body).["email"]
        return if isNull email then "" else email.ToString()
    }

/// The seam `authorise`/`reauthorise` close over with the real Google.Apis calls above. A test
/// calls this directly with fakes for both, so no test opens a browser, starts a loopback
/// listener, or reaches the network - see `GoogleAuthorizationTests`.
///
/// Takes `accountId` as a parameter rather than minting one itself, so re-authorising an
/// existing account can reuse its id as the same OAuth datastore key - the credential row for
/// that id is simply overwritten, and the Accounts row (its `DefaultInvoiceCalendar` included)
/// never has to move to a new identity.
///
/// Returns raw strings (email, accountId): this is the outer ring, so the domain's
/// `GoogleEmail`/`GoogleAccountId` wrapping happens at the composition root, the same as every
/// other adapter in this codebase.
let authoriseWith
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (runConsentFlow: string -> string -> IDataStore -> Task<UserCredential>)
    (fetchAccountEmail: UserCredential -> Task<string>)
    (clientSecretJson: string)
    (accountId: string)
    ()
    : Result<string * string, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise

    handleError {
        try
            let dataStore = GoogleCredentialDataStore(handleError, getCredentialCollection) :> IDataStore

            // These two steps convert their own known failure shapes into a plain Result value
            // rather than letting them raise - so cancelling consent, a malformed secret, or a
            // missing email pass through the outer handleError block unlogged, the same idiom
            // PdfDocumentReader.readContent uses for a missing file.
            let! credential =
                try
                    runConsentFlow clientSecretJson accountId dataStore
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                    |> Ok
                with
                | :? TokenResponseException as ex ->
                    Error(MyDogsbodyException(action, "The consent flow was cancelled or denied.", ex))
                | :? Newtonsoft.Json.JsonException as ex ->
                    Error(MyDogsbodyException(action, "The stored Google client secret is malformed.", ex))
                | :? FormatException as ex ->
                    Error(MyDogsbodyException(action, "The stored Google client secret is malformed.", ex))

            let! email =
                fetchAccountEmail credential
                |> Async.AwaitTask
                |> Async.RunSynchronously
                |> fun value ->
                    if String.IsNullOrWhiteSpace value then
                        Error(
                            MyDogsbodyException(
                                action,
                                "The authorised account's email address could not be read.",
                                ApplicationException "no email returned"
                            )
                        )
                    else
                        Ok value

            return email, accountId
        with
        | :? HttpListenerException as ex ->
            return! MyDogsbodyException(action, "The loopback port is already in use.", ex)
        | :? OperationCanceledException as ex ->
            return! MyDogsbodyException(action, "The consent flow timed out.", ex)
        | ex ->
            return! MyDogsbodyException(action, "Authorisation failed.", ex)
    }

/// The composition root's entry point for registering a new account - the real consent flow and
/// the real email fetch, bound, with a freshly minted account id.
let authorise
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (clientSecretJson: string)
    ()
    : Result<string * string, MyDogsbodyException> =
    let accountId = string (ObjectId.NewObjectId())

    authoriseWith
        handleError
        getCredentialCollection
        runRealConsentFlow
        fetchRealAccountEmail
        clientSecretJson
        accountId
        ()

/// The composition root's entry point for re-authorising an account whose token has expired or
/// been revoked - the same real calls, reusing the account's existing id.
let reauthorise
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (clientSecretJson: string)
    (accountId: string)
    ()
    : Result<string * string, MyDogsbodyException> =
    authoriseWith
        handleError
        getCredentialCollection
        runRealConsentFlow
        fetchRealAccountEmail
        clientSecretJson
        accountId
        ()

/// Loads a working `UserCredential` for an already-registered account, from its stored token -
/// never `GoogleWebAuthorizationBroker.AuthorizeAsync`, which would open a browser if the token
/// were ever missing. `GoogleCalendarClient.listCalendars` (via `GoogleAccountApiFactory`) uses
/// this to get a credential for `ListCalendars`/`GetCalendarsFor`; the returned `UserCredential`
/// refreshes its access token silently, using the stored refresh token, if it has expired - no
/// user interaction either way.
///
/// `Message = "No stored credential for this account."` is the one case
/// `GoogleAccountApiMappers` maps to `NotAuthorised`, ahead of anything `listCalendars` itself
/// could report.
let loadCredential
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (clientSecretJson: string)
    (accountId: string)
    : Result<UserCredential, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise

    handleError {
        try
            use stream = new MemoryStream(Encoding.UTF8.GetBytes clientSecretJson)
            let secrets = GoogleClientSecrets.FromStream(stream).Secrets
            let dataStore = GoogleCredentialDataStore(handleError, getCredentialCollection) :> IDataStore

            let flow =
                new GoogleAuthorizationCodeFlow(
                    GoogleAuthorizationCodeFlow.Initializer(ClientSecrets = secrets, Scopes = scopes, DataStore = dataStore)
                )

            let stored = dataStore.GetAsync<TokenResponse>(accountId) |> Async.AwaitTask |> Async.RunSynchronously

            let! storedToken =
                if isNull (box stored) then
                    Error(
                        MyDogsbodyException(
                            action,
                            "No stored credential for this account.",
                            ApplicationException "no stored token"
                        )
                    )
                else
                    Ok stored

            return UserCredential(flow, accountId, storedToken)
        with ex ->
            return! MyDogsbodyException(action, "Authorisation failed.", ex)
    }

/// Deletes the stored token for an account being removed (requirements.md: "deletes its local
/// token and its record"). `GoogleAccountApiFactory`'s `RemoveAccount` calls this alongside
/// `GoogleAccountStore.removeOne`. Thin enough, and non-critical enough on failure (a leftover
/// token row for a deleted account is inert), that it does not merit its own `ActionNames` entry;
/// any store failure surfaces as a raised `MyDogsbodyException` from `GoogleCredentialDataStore`.
let removeStoredToken
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (accountId: string)
    : unit =
    let dataStore = GoogleCredentialDataStore(handleError, getCredentialCollection) :> IDataStore
    dataStore.DeleteAsync<TokenResponse>(accountId) |> Async.AwaitTask |> Async.RunSynchronously
