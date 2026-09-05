module MyDogsbody.Tests.Integrations.Google.GoogleAccountStoreTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database

/// No-op logger, so these tests never reach Logging.db.
let private handleError = HandleErrorBuilder(fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private accountId value = GoogleAccountId.create value |> valueOrFail
let private email value = GoogleEmail.create value |> valueOrFail
let private calendarId value = CalendarId.create value |> valueOrFail

let private account id emailValue defaultCalendar : RegisteredGoogleAccount =
    {
        Id = accountId id
        EmailAddress = email emailValue
        DefaultInvoiceCalendar = defaultCalendar
        NeedsReauthorisation = false
    }

/// Fresh disposable database per test, no state shared - the same shape GoogleCredentialStoreTests
/// established for this integration.
let private withStore
    (test: (unit -> Database.Types.GoogleAccountsCollection) -> (unit -> Database.Types.GoogleClientSecretCollection) -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test context.GetAccountCollection context.GetClientSecretCollection
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) ->
        failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

// ---------- accounts ----------

[<Fact; Trait("Level", "Integration")>]
let ``getAll returns an empty list for a fresh database`` () =
    withStore (fun getAccounts _ ->
        let stored = GoogleAccountStore.getAll handleError getAccounts () |> okOrFail "getAll"
        Assert.Empty stored
    )

[<Fact; Trait("Level", "Integration")>]
let ``saveOne then getAll returns the row with every field mapped back`` () =
    withStore (fun getAccounts _ ->
        let saved =
            account "507f1f77bcf86cd799439011" "person@gmail.com" None
            |> GoogleAccountStore.saveOne handleError getAccounts
            |> okOrFail "saveOne"

        let stored = GoogleAccountStore.getAll handleError getAccounts () |> okOrFail "getAll"
        let readBack = Assert.Single stored

        Assert.Equal(saved.Id, readBack.Id)
        Assert.Equal("person@gmail.com", GoogleEmail.value readBack.EmailAddress)
        Assert.Equal(None, readBack.DefaultInvoiceCalendar)
        Assert.False readBack.NeedsReauthorisation
    )

[<Fact; Trait("Level", "Integration")>]
let ``saving an account twice updates rather than duplicating`` () =
    withStore (fun getAccounts _ ->
        let original = account "507f1f77bcf86cd799439011" "person@gmail.com" None

        original |> GoogleAccountStore.saveOne handleError getAccounts |> okOrFail "saveOne first" |> ignore

        { original with DefaultInvoiceCalendar = Some (calendarId "cal-1") }
        |> GoogleAccountStore.saveOne handleError getAccounts
        |> okOrFail "saveOne second"
        |> ignore

        let stored = GoogleAccountStore.getAll handleError getAccounts () |> okOrFail "getAll"
        let readBack = Assert.Single stored
        Assert.Equal(Some (calendarId "cal-1"), readBack.DefaultInvoiceCalendar)
    )

[<Fact; Trait("Level", "Integration")>]
let ``removeOne deletes the addressed account and reports true`` () =
    withStore (fun getAccounts _ ->
        let saved =
            account "507f1f77bcf86cd799439011" "person@gmail.com" None
            |> GoogleAccountStore.saveOne handleError getAccounts
            |> okOrFail "saveOne"

        let removed = GoogleAccountStore.removeOne handleError getAccounts saved.Id |> okOrFail "removeOne"

        Assert.True removed
        Assert.Empty(GoogleAccountStore.getAll handleError getAccounts () |> okOrFail "getAll")
    )

[<Fact; Trait("Level", "Integration")>]
let ``removeOne reports false for an identifier no row carries`` () =
    withStore (fun getAccounts _ ->
        let removed =
            GoogleAccountStore.removeOne handleError getAccounts (accountId "507f1f77bcf86cd799439099")
            |> okOrFail "removeOne"

        Assert.False removed
    )

// ---------- client secret ----------

[<Fact; Trait("Level", "Integration")>]
let ``loadClientSecret returns None when nothing has been saved`` () =
    withStore (fun _ getSecret ->
        let loaded = GoogleAccountStore.loadClientSecret handleError getSecret () |> okOrFail "loadClientSecret"
        Assert.Equal(None, loaded)
    )

[<Fact; Trait("Level", "Integration")>]
let ``saveClientSecret then loadClientSecret returns the saved value`` () =
    withStore (fun _ getSecret ->
        GoogleAccountStore.saveClientSecret handleError getSecret "the-client-secret"
        |> okOrFail "saveClientSecret"

        let loaded = GoogleAccountStore.loadClientSecret handleError getSecret () |> okOrFail "loadClientSecret"
        Assert.Equal(Some "the-client-secret", loaded)
    )

[<Fact; Trait("Level", "Integration")>]
let ``saveClientSecret replaces the existing value, remaining a single row`` () =
    withStore (fun _ getSecret ->
        GoogleAccountStore.saveClientSecret handleError getSecret "first" |> okOrFail "saveClientSecret first"
        GoogleAccountStore.saveClientSecret handleError getSecret "second" |> okOrFail "saveClientSecret second"

        let loaded = GoogleAccountStore.loadClientSecret handleError getSecret () |> okOrFail "loadClientSecret"
        Assert.Equal(Some "second", loaded)
        Assert.Equal(1, getSecret().Count())
    )

// ---------- error paths ----------

[<Fact; Trait("Level", "Unit")>]
let ``getAll reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleAccountsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    match GoogleAccountStore.getAll recordingHandleError failingGetter () with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.getAll, ex.ActionName)
        Assert.Equal("Failed to retrieve all Google accounts.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``saveOne reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleAccountsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    match
        account "507f1f77bcf86cd799439011" "person@gmail.com" None
        |> GoogleAccountStore.saveOne recordingHandleError failingGetter
    with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.saveOne, ex.ActionName)
        Assert.Equal("Failed to save the Google account.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``removeOne reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleAccountsCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    match GoogleAccountStore.removeOne recordingHandleError failingGetter (accountId "507f1f77bcf86cd799439011") with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.removeOne, ex.ActionName)
        Assert.Equal("Failed to remove the Google account.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``loadClientSecret reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleClientSecretCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    match GoogleAccountStore.loadClientSecret recordingHandleError failingGetter () with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.loadClientSecret, ex.ActionName)
        Assert.Equal("Failed to load the Google client secret.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``saveClientSecret reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter: unit -> Database.Types.GoogleClientSecretCollection =
        fun () -> raise (InvalidOperationException "database is gone")

    match GoogleAccountStore.saveClientSecret recordingHandleError failingGetter "secret" with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.saveClientSecret, ex.ActionName)
        Assert.Equal("Failed to save the Google client secret.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
