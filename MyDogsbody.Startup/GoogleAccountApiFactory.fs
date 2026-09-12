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

let createGoogleAccountApi (handleError: HandleErrorBuilder) (googleContext: GoogleDatabaseContext) : GoogleAccountApi =

    // ---------- Storage dependencies: GoogleAccountStore speaks MyDogsbodyException; every
    // dependency type here speaks CalendarError, so each is translated on the way out. ----------

    let loadClientSecret: LoadClientSecret =
        fun () ->
            GoogleAccountStore.loadClientSecret handleError googleContext.GetClientSecretCollection ()
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let saveClientSecretDependency: SaveClientSecret =
        fun secret ->
            GoogleAccountStore.saveClientSecret handleError googleContext.GetClientSecretCollection secret
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let listGoogleAccounts: ListGoogleAccounts =
        fun () ->
            GoogleAccountStore.getAll handleError googleContext.GetAccountCollection ()
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let saveGoogleAccount: SaveGoogleAccount =
        fun account ->
            GoogleAccountStore.saveOne handleError googleContext.GetAccountCollection account
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    let removeGoogleAccountDependency: RemoveGoogleAccount =
        fun accountId ->
            GoogleAccountStore.removeOne handleError googleContext.GetAccountCollection accountId
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    /// The client secret's raw string, for the adapter calls that need it directly rather than
    /// through a dependency type. A missing secret folds into `ClientSecretMissing` - the same
    /// case `RegisterGoogleAccountWorkflow`'s own check produces, since by the time any of these
    /// three dependencies run, that check has already passed.
    let loadClientSecretValue () : Result<string, CalendarError> =
        result {
            let! secret = loadClientSecret ()

            return!
                match secret with
                | Some value -> Ok value
                | None -> Error ClientSecretMissing
        }

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

    /// Throws away the token a completed consent flow left behind when the registration it was
    /// for is refused. Same adapter `RemoveAccount` uses to delete a removed account's token -
    /// an authorisation with no account row is exactly what removing an account leaves behind.
    let discardAuthorisation: DiscardAuthorisation =
        fun accountId ->
            GoogleAuthorization.removeStoredToken
                handleError
                googleContext.GetCredentialCollection
                (GoogleAccountId.value accountId)
            |> Result.mapError GoogleAccountApiMappers.toStoreError

    /// `loadCredential` never opens a browser - it fails with `NotAuthorised` if no valid
    /// stored token exists, rather than starting a consent flow the caller did not ask for.
    let listCalendarsDependency: ListCalendars =
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
                    GoogleCalendarClient.listCalendars handleError credential ()
                    |> Result.mapError (GoogleAccountApiMappers.toListCalendarsError accountId)
            }

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

        RegisterAccount =
            fun () ->
                RegisterGoogleAccountWorkflow.registerGoogleAccount
                    loadClientSecret
                    listGoogleAccounts
                    authoriseAccount
                    discardAuthorisation
                    saveGoogleAccount
                    ()
                |> Result.map GoogleAccountApiMappers.toGoogleAccountUiType
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.registerAccount)

        ReauthoriseAccount =
            fun id ->
                ReauthoriseGoogleAccountWorkflow.reauthoriseGoogleAccount
                    listGoogleAccounts
                    reauthoriseAccountDependency
                    saveGoogleAccount
                    id
                |> Result.map GoogleAccountApiMappers.toGoogleAccountUiType
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.reauthoriseAccount)

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

        GetCalendarsFor =
            fun id ->
                result {
                    let! accountId = GoogleAccountId.create id |> Result.mapError GoogleAccountIdInvalid
                    let! calendars = listCalendarsDependency accountId
                    return calendars |> List.map GoogleAccountApiMappers.toCalendarUiType
                }
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.getCalendarsFor)

        SetDefaultInvoiceCalendar =
            fun accountId calendarId ->
                SetDefaultInvoiceCalendarWorkflow.setDefaultInvoiceCalendar
                    listGoogleAccounts
                    listCalendarsDependency
                    saveGoogleAccount
                    accountId
                    calendarId
                |> Result.map GoogleAccountApiMappers.toGoogleAccountUiType
                |> Result.mapError (
                    toException ActionNames.MyDogsbody.Startup.GoogleAccountApi.setDefaultInvoiceCalendar
                )
    }
