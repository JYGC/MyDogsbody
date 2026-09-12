/// Where the abstract meets the real: the adapters that satisfy each Calendar dependency
/// function type, the workflows partially applied over them, and the translation between the
/// two error types.
///
/// This is the only place that knows both the Google integration and the domain, and the only
/// place the two error types meet. Dependencies are leading parameters, so a test supplies a
/// temp database context; no module-level bindings, so nothing opens a file on import.
module MyDogsbody.Startup.GoogleAccountApiFactory

open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database.Types
open MyDogsbody.UI.Types

// ---------- Storage dependencies, as the composition root binds them. GoogleAccountStore and
// GoogleAuthorization.removeStoredToken speak MyDogsbodyException; every dependency type here
// speaks CalendarError, so each is translated on the way out.
//
// Public so that GoogleAccountDependencyContractTests and the E2E harness run these bindings rather
// than copies of them. Only registering, re-authorising and choosing a calendar use SaveGoogleAccount
// and DiscardAuthorisation, and each of those goes through Google first, so no test of
// `createGoogleAccountApi` reaches them - the members further down (`registerAccountWith` and the
// rest) are how a test does. With copies in both places, a SaveGoogleAccount that never saved, and
// a DiscardAuthorisation that never discarded, each passed the whole suite (PR review series 2
// round 8). ----------

/// The stored client secret, if one has been supplied.
let bindLoadClientSecret (handleError: HandleErrorBuilder) (googleContext: GoogleDatabaseContext) : LoadClientSecret =
    fun () ->
        GoogleAccountStore.loadClientSecret handleError googleContext.GetClientSecretCollection ()
        |> Result.mapError GoogleAccountApiMappers.toStoreError

let bindSaveClientSecret (handleError: HandleErrorBuilder) (googleContext: GoogleDatabaseContext) : SaveClientSecret =
    fun secret ->
        GoogleAccountStore.saveClientSecret handleError googleContext.GetClientSecretCollection secret
        |> Result.mapError GoogleAccountApiMappers.toStoreError

let bindListGoogleAccounts (handleError: HandleErrorBuilder) (googleContext: GoogleDatabaseContext) : ListGoogleAccounts =
    fun () ->
        GoogleAccountStore.getAll handleError googleContext.GetAccountCollection ()
        |> Result.mapError GoogleAccountApiMappers.toStoreError

let bindSaveGoogleAccount (handleError: HandleErrorBuilder) (googleContext: GoogleDatabaseContext) : SaveGoogleAccount =
    fun account ->
        GoogleAccountStore.saveOne handleError googleContext.GetAccountCollection account
        |> Result.mapError GoogleAccountApiMappers.toStoreError

let bindRemoveGoogleAccount (handleError: HandleErrorBuilder) (googleContext: GoogleDatabaseContext) : RemoveGoogleAccount =
    fun accountId ->
        GoogleAccountStore.removeOne handleError googleContext.GetAccountCollection accountId
        |> Result.mapError GoogleAccountApiMappers.toStoreError

/// Throws away the token a completed consent flow left behind when the registration it was for is
/// refused. Same adapter `RemoveAccount` uses to delete a removed account's token - an authorisation
/// with no account row is exactly what removing an account leaves behind.
let bindDiscardAuthorisation
    (handleError: HandleErrorBuilder)
    (googleContext: GoogleDatabaseContext)
    : DiscardAuthorisation =
    fun accountId ->
        GoogleAuthorization.removeStoredToken
            handleError
            googleContext.GetCredentialCollection
            (GoogleAccountId.value accountId)
        |> Result.mapError GoogleAccountApiMappers.toStoreError

/// The client secret's raw string, for the adapter calls that need it directly rather than
/// through a dependency type. A missing secret folds into `ClientSecretMissing` - the same
/// case `RegisterGoogleAccountWorkflow`'s own check produces, since by the time any of the
/// adapter calls that need it run, that check has already passed.
let private clientSecretValueFrom (loadClientSecret: LoadClientSecret) () : Result<string, CalendarError> =
    result {
        let! secret = loadClientSecret ()

        return!
            match secret with
            | Some value -> Ok value
            | None -> Error ClientSecretMissing
    }

/// `ListCalendars` as the composition root binds it: the stored client secret, the account's stored
/// token, then the calendar list - every failure on the way translated by `toListCalendarsError`,
/// the translation that can name the account in `NotAuthorised`. `loadCredential` never opens a
/// browser: it fails with `NotAuthorised` if no valid stored token exists, rather than starting a
/// consent flow the caller did not ask for.
///
/// The calendar call is a parameter - `GoogleCalendarClient.listCalendars` in production - so that
/// `ListCalendarsDependencyContractTests` runs this binding over `listCalendarsVia` and a stubbed
/// `HttpMessageHandler`. It used to run a copy of it, and a mis-wired translation here passed every
/// test in the suite (PR review series 2 round 7).
let bindListCalendars
    (handleError: HandleErrorBuilder)
    (googleContext: GoogleDatabaseContext)
    (listCalendarsWith:
        HandleErrorBuilder
            -> Google.Apis.Http.IConfigurableHttpClientInitializer
            -> unit
            -> Result<AvailableCalendar list, MyDogsbodyException>)
    : ListCalendars =
    let loadClientSecretValue = clientSecretValueFrom (bindLoadClientSecret handleError googleContext)

    fun accountId ->
        result {
            let! secret = loadClientSecretValue ()

            let! credential =
                GoogleAuthorization.loadCredential
                    handleError
                    googleContext.GetCredentialCollection
                    secret
                    (GoogleAccountId.value accountId)
                |> Result.mapError (GoogleAccountApiMappers.toListCalendarsError accountId)

            return!
                listCalendarsWith handleError (credential :> Google.Apis.Http.IConfigurableHttpClientInitializer) ()
                |> Result.mapError (GoogleAccountApiMappers.toListCalendarsError accountId)
        }

// ---------- The members that go through Google, as the composition root composes them: each
// workflow over the storage bindings above, its answer mapped to the UI record and its error
// translated, with the Google-facing dependency a parameter. `createGoogleAccountApi` hands them
// the real consent flow and calendar client.
//
// Public so that GoogleAccountApiFactoryTests and the E2E harness hand them fakes in Google's place
// and run this composition, rather than a copy of it. Nothing else reaches these members past
// their refusals: the real consent flow needs a system browser, and the real calendar client the
// network. With a copy in the harness, a RegisterAccount handed a discard that did nothing - so
// that every refused registration left its refresh token stored - passed the whole suite (PR review
// series 2 rounds 8 and 9). ----------

/// `RegisterAccount`, over whichever consent flow it is handed.
let registerAccountWith
    (handleError: HandleErrorBuilder)
    (googleContext: GoogleDatabaseContext)
    (authoriseAccount: AuthoriseAccount)
    : unit -> Result<GoogleAccountUiType, MyDogsbodyException> =
    let loadClientSecret = bindLoadClientSecret handleError googleContext
    let listGoogleAccounts = bindListGoogleAccounts handleError googleContext
    let discardAuthorisation = bindDiscardAuthorisation handleError googleContext
    let saveGoogleAccount = bindSaveGoogleAccount handleError googleContext

    fun () ->
        RegisterGoogleAccountWorkflow.registerGoogleAccount
            loadClientSecret
            listGoogleAccounts
            authoriseAccount
            discardAuthorisation
            saveGoogleAccount
            ()
        |> Result.map GoogleAccountApiMappers.toGoogleAccountUiType
        |> Result.mapError (
            GoogleAccountApiMappers.toMyDogsbodyException
                ActionNames.MyDogsbody.Startup.GoogleAccountApi.registerAccount
        )

/// `ReauthoriseAccount`, over whichever consent flow it is handed.
let reauthoriseAccountWith
    (handleError: HandleErrorBuilder)
    (googleContext: GoogleDatabaseContext)
    (reauthoriseAccount: ReauthoriseAccount)
    : string -> Result<GoogleAccountUiType, MyDogsbodyException> =
    let listGoogleAccounts = bindListGoogleAccounts handleError googleContext
    let saveGoogleAccount = bindSaveGoogleAccount handleError googleContext

    fun accountId ->
        ReauthoriseGoogleAccountWorkflow.reauthoriseGoogleAccount
            listGoogleAccounts
            reauthoriseAccount
            saveGoogleAccount
            accountId
        |> Result.map GoogleAccountApiMappers.toGoogleAccountUiType
        |> Result.mapError (
            GoogleAccountApiMappers.toMyDogsbodyException
                ActionNames.MyDogsbody.Startup.GoogleAccountApi.reauthoriseAccount
        )

/// `GetCalendarsFor`, over whichever calendar list it is handed. It needs no storage of its own:
/// the production `ListCalendars` binding reads the stored secret and token itself.
let getCalendarsForWith (listCalendars: ListCalendars) : string -> Result<CalendarUiType list, MyDogsbodyException> =
    fun id ->
        result {
            let! accountId = GoogleAccountId.create id |> Result.mapError GoogleAccountIdInvalid
            let! calendars = listCalendars accountId
            return calendars |> List.map GoogleAccountApiMappers.toCalendarUiType
        }
        |> Result.mapError (
            GoogleAccountApiMappers.toMyDogsbodyException
                ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor
        )

/// `SetDefaultInvoiceCalendar`, over whichever calendar list it is handed - the list the chosen
/// calendar must still be in before it is stored.
let setDefaultInvoiceCalendarWith
    (handleError: HandleErrorBuilder)
    (googleContext: GoogleDatabaseContext)
    (listCalendars: ListCalendars)
    : string -> string -> Result<GoogleAccountUiType, MyDogsbodyException> =
    let listGoogleAccounts = bindListGoogleAccounts handleError googleContext
    let saveGoogleAccount = bindSaveGoogleAccount handleError googleContext

    fun accountId calendarId ->
        SetDefaultInvoiceCalendarWorkflow.setDefaultInvoiceCalendar
            listGoogleAccounts
            listCalendars
            saveGoogleAccount
            accountId
            calendarId
        |> Result.map GoogleAccountApiMappers.toGoogleAccountUiType
        |> Result.mapError (
            GoogleAccountApiMappers.toMyDogsbodyException
                ActionNames.MyDogsbody.Startup.GoogleAccountApi.setDefaultInvoiceCalendar
        )

let createGoogleAccountApi (handleError: HandleErrorBuilder) (googleContext: GoogleDatabaseContext) : GoogleAccountApi =

    // ---------- Storage dependencies: the bindings above, the ones the contract suite runs. ----------

    let loadClientSecret = bindLoadClientSecret handleError googleContext
    let saveClientSecretDependency = bindSaveClientSecret handleError googleContext
    let listGoogleAccounts = bindListGoogleAccounts handleError googleContext
    let removeGoogleAccountDependency = bindRemoveGoogleAccount handleError googleContext

    let loadClientSecretValue = clientSecretValueFrom loadClientSecret

    // ---------- Authorisation: GoogleAuthorization/GoogleCalendarClient. ----------

    let authoriseAccount: AuthoriseAccount =
        fun () ->
            result {
                let! secret = loadClientSecretValue ()

                let! (emailRaw, accountIdRaw) =
                    GoogleAuthorization.authorise handleError googleContext.GetCredentialCollection secret ()
                    |> Result.mapError GoogleAccountApiMappers.toAuthorisationError

                let! email = GoogleEmail.create emailRaw |> Result.mapError (fun _ -> AccountEmailUnavailable)
                let! accountId = GoogleAccountId.create accountIdRaw |> Result.mapError GoogleAccountIdInvalid
                return email, accountId
            }

    let reauthoriseAccountDependency: ReauthoriseAccount =
        fun accountId ->
            result {
                let! secret = loadClientSecretValue ()

                let! (emailRaw, _) =
                    GoogleAuthorization.reauthorise
                        handleError
                        googleContext.GetCredentialCollection
                        secret
                        (GoogleAccountId.value accountId)
                        ()
                    |> Result.mapError GoogleAccountApiMappers.toAuthorisationError

                return! GoogleEmail.create emailRaw |> Result.mapError (fun _ -> AccountEmailUnavailable)
            }

    let listCalendarsDependency: ListCalendars =
        bindListCalendars handleError googleContext GoogleCalendarClient.listCalendars

    // ---------- Workflows, partially applied over the dependencies above. ----------

    let toException = GoogleAccountApiMappers.toMyDogsbodyException

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

        RegisterAccount = registerAccountWith handleError googleContext authoriseAccount

        ReauthoriseAccount = reauthoriseAccountWith handleError googleContext reauthoriseAccountDependency

        RemoveAccount =
            fun id ->
                RemoveGoogleAccountWorkflow.removeGoogleAccount removeGoogleAccountDependency id
                |> Result.map (fun () ->
                    // The account row is already gone by here, so a token that will not delete
                    // must not report the removal as having failed - the user would be told to
                    // retry something that has already happened, and the table would not reload.
                    // `handleError` has logged it; the leftover row is inert. Discarded as a
                    // value, deliberately, rather than left free to raise past this Result.
                    GoogleAuthorization.removeStoredToken handleError googleContext.GetCredentialCollection id
                    |> ignore)
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.removeAccount)

        GetCalendarsFor = getCalendarsForWith listCalendarsDependency

        SetDefaultInvoiceCalendar = setDefaultInvoiceCalendarWith handleError googleContext listCalendarsDependency
    }
