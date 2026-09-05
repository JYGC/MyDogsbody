/// The Google account adapter: the client secret (a single row) and account CRUD, over the
/// integration's own LiteDB `Accounts` and `ClientSecret` collections.
///
/// Outer ring, so the shape is the established one - handleError first, the collection getter
/// next, input last, Result<'T, MyDogsbodyException> out. There is no domain error type here:
/// GoogleAccountApiFactory does the translating, the same way MailAccountApiFactory translates
/// ThunderbirdStore's exceptions into MailAccountError.
module MyDogsbody.Integrations.Google.GoogleAccountStore

open System
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database.Types
open MyDogsbody.Integrations.Google.Database.Models

/// The client secret is a single row; a fixed id means "the client secret" always addresses the
/// same document rather than a query that could somehow match more than one.
let private clientSecretRowId = ObjectId("000000000000000000000001")

/// A row that cannot be mapped back is a data-integrity failure, not something a user did, so
/// it is raised and caught like any other unexpected failure rather than returned as a value.
let private mapOrRaise (entity: GoogleAccountEntity) =
    match GoogleEntityMappers.toRegisteredAccount entity with
    | Ok account -> account
    | Error reason -> raise (InvalidOperationException $"Stored Google account is unusable: {reason}")

let loadClientSecret
    (handleError: HandleErrorBuilder)
    (getClientSecretCollection: unit -> GoogleClientSecretCollection)
    ()
    : Result<string option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.loadClientSecret

    handleError {
        try
            let existing = getClientSecretCollection().FindById clientSecretRowId
            return if isNull (box existing) then None else Some existing.Secret
        with ex ->
            return! MyDogsbodyException(action, "Failed to load the Google client secret.", ex)
    }

let saveClientSecret
    (handleError: HandleErrorBuilder)
    (getClientSecretCollection: unit -> GoogleClientSecretCollection)
    (secret: string)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.saveClientSecret

    handleError {
        try
            let entity = GoogleClientSecretEntity(Id = clientSecretRowId, Secret = secret)
            getClientSecretCollection().Upsert entity |> ignore
            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to save the Google client secret.", ex)
    }

let getAll
    (handleError: HandleErrorBuilder)
    (getAccountCollection: unit -> GoogleAccountsCollection)
    ()
    : Result<RegisteredGoogleAccount list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.getAll

    handleError {
        try
            return
                getAccountCollection().Query().ToEnumerable()
                |> Seq.map mapOrRaise
                |> Seq.toList
        with ex ->
            return! MyDogsbodyException(action, "Failed to retrieve all Google accounts.", ex)
    }

/// Upserts by the account's own id, so registering an already-saved account (re-authorising it,
/// or choosing its default calendar) updates the same row rather than duplicating it.
let saveOne
    (handleError: HandleErrorBuilder)
    (getAccountCollection: unit -> GoogleAccountsCollection)
    (account: RegisteredGoogleAccount)
    : Result<RegisteredGoogleAccount, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.saveOne

    handleError {
        try
            let entity = GoogleEntityMappers.toEntity account
            getAccountCollection().Upsert entity |> ignore
            return mapOrRaise entity
        with ex ->
            return! MyDogsbodyException(action, "Failed to save the Google account.", ex)
    }

/// Ok false means no row carried that identifier - reported rather than silently succeeding, so
/// the caller can decide that absence means "not registered".
let removeOne
    (handleError: HandleErrorBuilder)
    (getAccountCollection: unit -> GoogleAccountsCollection)
    (accountId: GoogleAccountId)
    : Result<bool, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.removeOne

    handleError {
        try
            let objectId = ObjectId(GoogleAccountId.value accountId)
            return getAccountCollection().Delete objectId
        with ex ->
            return! MyDogsbodyException(action, "Failed to remove the Google account.", ex)
    }
