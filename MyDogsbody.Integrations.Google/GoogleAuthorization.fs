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
        // Whatever the parse trips on - text that is not JSON, JSON that is not an OAuth client
        // secret (a pasted service-account key: InvalidOperationException), an empty paste
        // (NullReferenceException) - it means one thing: what is stored is not a usable client
        // secret. FormatException is the shape `authoriseWith` names as malformed, so each of those
        // is reported as that, the same rule `loadCredential` applies to the very same parse.
        let secrets =
            try
                use stream = new MemoryStream(Encoding.UTF8.GetBytes clientSecretJson)
                GoogleClientSecrets.FromStream(stream).Secrets
            with ex ->
                raise (FormatException("The client secret is not a Google OAuth client secret.", ex))

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
            //
            // Both are awaited with `.GetAwaiter().GetResult()`, never `Async.AwaitTask |>
            // Async.RunSynchronously`. The consent flow is an async method, so its failures arrive
            // as a faulted Task, and AwaitTask surfaces that as an AggregateException - which no
            // catch here or below matches, so every named failure used to reach the user as
            // "One or more errors occurred. (...)", logged. GetResult rethrows the original.
            let! credential =
                try
                    (runConsentFlow clientSecretJson accountId dataStore).GetAwaiter().GetResult() |> Ok
                with
                // "access_denied" is the OAuth error code for a user who pressed Cancel or said no.
                // A TokenResponseException carrying any other code (a failed code exchange -
                // "invalid_client" for a wrong or rotated client_secret) happened AFTER the user
                // consented, so it is not their choice to report back to them; it falls to the
                // logged catch-all below, which keeps Google's code in the inner exception.
                | :? TokenResponseException as ex when not (isNull ex.Error) && ex.Error.Error = "access_denied" ->
                    Error(MyDogsbodyException(action, "The consent flow was cancelled or denied.", ex))
                | :? Newtonsoft.Json.JsonException as ex ->
                    Error(MyDogsbodyException(action, "The stored Google client secret is malformed.", ex))
                | :? FormatException as ex ->
                    Error(MyDogsbodyException(action, "The stored Google client secret is malformed.", ex))

            let! email =
                (fetchAccountEmail credential).GetAwaiter().GetResult()
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

/// Deletes the stored token for an account being removed (requirements.md: "deletes its local
/// token and its record"), and discards the token a refused registration left behind - both the
/// refusals `RegisterGoogleAccountWorkflow` makes (its `DiscardAuthorisation` dependency) and the
/// failures `authoriseNewAccountWith` meets after consent, before the workflow ever sees an id.
///
/// Outer-ring shape, like every other function here. It used to return `unit` on the reasoning
/// that a leftover token row is inert - but `GoogleCredentialDataStore` is exception-based by
/// construction (`IDataStore` is not `Result`-based), so an unreachable store *raised* straight
/// out of `GoogleAccountApi.RemoveAccount`, past its declared `Result<unit, MyDogsbodyException>`
/// and past the UI's error branch. A failure here is now a value like every other, and its
/// caller decides what it is worth; nothing escapes as control flow.
let removeStoredToken
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (accountId: string)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.removeStoredToken

    handleError {
        try
            let dataStore = GoogleCredentialDataStore(handleError, getCredentialCollection) :> IDataStore
            dataStore.DeleteAsync<TokenResponse>(accountId) |> Async.AwaitTask |> Async.RunSynchronously
            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to delete the stored Google credential.", ex)
    }

/// `authoriseWith` for an account that does not exist yet, under an id minted for it - plus the
/// one clean-up only this adapter can do.
///
/// Consent persists a token under `accountId` before the email is read, so a failure from that
/// point on (the email unreadable, the userinfo call failing) leaves a token behind. An `Error`
/// never carries the id, so `RegisterGoogleAccountWorkflow`'s `DiscardAuthorisation` - which
/// covers every step *after* this one - cannot reach it, and neither can removing an account,
/// because no account row will ever carry this id. The id was minted for this one attempt and is
/// never handed out on failure, so nothing else can refer to a token under it: handing it back is
/// always safe. A failure before consent wrote anything finds nothing to delete, and that logs
/// nothing.
///
/// The removal's own result is discarded for the reason the workflow discards its discard: a
/// failed clean-up must not replace the answer the user needs, and its `handleError` has already
/// recorded it.
///
/// `reauthorise` deliberately does not go through this: its id belongs to a registered account,
/// and whether a failed re-authorisation should delete that account's token is a decision for the
/// change that makes re-authorisation reachable (nothing sets `NeedsReauthorisation` yet).
let authoriseNewAccountWith
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (runConsentFlow: string -> string -> IDataStore -> Task<UserCredential>)
    (fetchAccountEmail: UserCredential -> Task<string>)
    (clientSecretJson: string)
    (accountId: string)
    ()
    : Result<string * string, MyDogsbodyException> =
    match authoriseWith handleError getCredentialCollection runConsentFlow fetchAccountEmail clientSecretJson accountId () with
    | Ok authorised -> Ok authorised
    | Error ex ->
        removeStoredToken handleError getCredentialCollection accountId |> ignore
        Error ex

/// The composition root's entry point for registering a new account - the real consent flow and
/// the real email fetch, bound, with a freshly minted account id whose token is handed back if
/// the authorisation fails after consent (`authoriseNewAccountWith`).
let authorise
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (clientSecretJson: string)
    ()
    : Result<string * string, MyDogsbodyException> =
    let accountId = string (ObjectId.NewObjectId())

    authoriseNewAccountWith
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
/// could report; `"The stored Google client secret is malformed."` is the other message this
/// function chooses, and maps to `ClientSecretInvalid` - it happens before a single byte reaches
/// Google, so it is not a "could not reach Google Calendar" failure.
let loadCredential
    (handleError: HandleErrorBuilder)
    (getCredentialCollection: unit -> GoogleCredentialsCollection)
    (clientSecretJson: string)
    (accountId: string)
    : Result<UserCredential, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.loadCredential

    handleError {
        try
            // Parsing the stored client secret is the only thing this step does, so any failure
            // in it means exactly one thing: the stored secret is malformed. Named here rather
            // than left to the catch-all below, which reported a bare "Authorisation failed." for
            // a bad paste - the "obscure authorisation failure" requirements.md asks this edge
            // case not to become. Yielded as an Error value, so handleError passes it through
            // unlogged, the same treatment `authoriseWith` gives the identical failure.
            let! secrets =
                try
                    use stream = new MemoryStream(Encoding.UTF8.GetBytes clientSecretJson)
                    Ok (GoogleClientSecrets.FromStream(stream).Secrets)
                with ex ->
                    Error(MyDogsbodyException(action, "The stored Google client secret is malformed.", ex))

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
