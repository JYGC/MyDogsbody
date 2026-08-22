module MyDogsbody.Tests.Integrations.Thunderbird.ThunderbirdStoreTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Integrations.Thunderbird.Database.Types
open MyDogsbody.Integrations.Thunderbird.MailFolderReader

/// No-op logger, so these tests never reach Logging.db.
let private handleError = HandleErrorBuilder(fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error(ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

/// Fresh disposable database per test, no state shared between tests.
let private withContext (test: ThunderbirdDatabaseContext -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test context
    finally
        context.Dispose()

        try
            File.Delete databasePath
        with _ ->
            ()

let private account id storeDirectory : DiscoveredMailAccount =
    {
        Id = MailAccountId.create id |> valueOrFail
        ProfilePath = @"C:\profile"
        DisplayName = $"Account {id}"
        EmailAddresses = [ $"{id}@example.com" ]
        StoreFormat = Mbox
        StoreDirectory = storeDirectory
        StoreDirectoryExists = true
        Folders = [ { RelativePath = "INBOX"; DisplayName = "INBOX"; SizeBytes = 10L; IsScannable = true } ]
        CachedMessageCount = None
    }

// ---------- Profile root ----------

[<Fact; Trait("Level", "Integration")>]
let ``loadProfileRoot returns None for a fresh database`` () =
    withContext (fun context ->
        let actual = ThunderbirdStore.loadProfileRoot handleError context.GetProfileRootCollection () |> okOrFail "loadProfileRoot"
        Assert.Equal(None, actual))

[<Fact; Trait("Level", "Integration")>]
let ``saveProfileRoot then loadProfileRoot round trips the path`` () =
    withContext (fun context ->
        let path = ProfileRootPath.create @"C:\Thunderbird\Profiles" |> valueOrFail

        ThunderbirdStore.saveProfileRoot handleError context.GetProfileRootCollection path
        |> okOrFail "saveProfileRoot"

        let actual = ThunderbirdStore.loadProfileRoot handleError context.GetProfileRootCollection () |> okOrFail "loadProfileRoot"

        match actual with
        | Some readBack -> Assert.Equal(@"C:\Thunderbird\Profiles", ProfileRootPath.value readBack)
        | None -> Assert.Fail("Expected Some, but got None"))

[<Fact; Trait("Level", "Integration")>]
let ``saveProfileRoot replaces the previous value rather than accumulating`` () =
    withContext (fun context ->
        ProfileRootPath.create @"C:\First"
        |> valueOrFail
        |> ThunderbirdStore.saveProfileRoot handleError context.GetProfileRootCollection
        |> okOrFail "saveProfileRoot first"
        |> ignore

        ProfileRootPath.create @"C:\Second"
        |> valueOrFail
        |> ThunderbirdStore.saveProfileRoot handleError context.GetProfileRootCollection
        |> okOrFail "saveProfileRoot second"
        |> ignore

        let actual = ThunderbirdStore.loadProfileRoot handleError context.GetProfileRootCollection () |> okOrFail "loadProfileRoot"

        match actual with
        | Some readBack -> Assert.Equal(@"C:\Second", ProfileRootPath.value readBack)
        | None -> Assert.Fail("Expected Some, but got None"))

// ---------- Accounts ----------

[<Fact; Trait("Level", "Integration")>]
let ``saveMailAccounts then loadMailAccounts round trips accounts and their folders`` () =
    withContext (fun context ->
        let accounts = [ account @"C:\profile|account1" @"C:\profile\a1"; account @"C:\profile|account2" @"C:\profile\a2" ]

        ThunderbirdStore.saveMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection accounts
        |> okOrFail "saveMailAccounts"

        let readBack =
            ThunderbirdStore.loadMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection ()
            |> okOrFail "loadMailAccounts"

        Assert.Equal(2, readBack.Length)

        let first = readBack |> List.find (fun a -> MailAccountId.value a.Id = @"C:\profile|account1")

        Assert.Equal("Account C:\\profile|account1", first.DisplayName)
        let folder = Assert.Single first.Folders
        Assert.Equal("INBOX", folder.RelativePath))

[<Fact; Trait("Level", "Integration")>]
let ``saveMailAccounts replaces the previous set rather than accumulating`` () =
    withContext (fun context ->
        [ account @"C:\profile|account1" @"C:\profile\a1" ]
        |> ThunderbirdStore.saveMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection
        |> okOrFail "saveMailAccounts first"
        |> ignore

        [ account @"C:\profile|account2" @"C:\profile\a2" ]
        |> ThunderbirdStore.saveMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection
        |> okOrFail "saveMailAccounts second"
        |> ignore

        let readBack =
            ThunderbirdStore.loadMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection ()
            |> okOrFail "loadMailAccounts"

        let only = Assert.Single readBack
        Assert.Equal(@"C:\profile|account2", MailAccountId.value only.Id)

        // The first account's folder must not linger in the Folders collection either.
        let allFolders = context.GetFoldersCollection().FindAll() |> Seq.toList
        Assert.All(allFolders, fun f -> Assert.Equal(@"C:\profile|account2", f.AccountId)))

// ---------- Cached message count ----------
//
// `updateCachedMessageCount` was the one ThunderbirdStore function this change shipped with no
// test at any level - no Ok path, no not-found path, no ActionNames assertion - while every other
// function in the module has five to seven. It is what persists the figure the accounts table's
// "Message count" column shows, so an untested no-op here reads on screen as "Not counted yet"
// after the user has pressed Count messages and been told it succeeded.

[<Fact; Trait("Level", "Integration")>]
let ``updateCachedMessageCount stores the count and the time it was taken, leaving the rest of the account alone`` () =
    withContext (fun context ->
        let id = MailAccountId.create @"C:\profile|account1" |> valueOrFail
        let takenAt = DateTime(2026, 8, 20, 13, 45, 30, DateTimeKind.Utc)

        [ account @"C:\profile|account1" @"C:\profile\a1"; account @"C:\profile|account2" @"C:\profile\a2" ]
        |> ThunderbirdStore.saveMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection
        |> okOrFail "saveMailAccounts"

        ThunderbirdStore.updateCachedMessageCount handleError context.GetAccountsCollection id 42 takenAt
        |> okOrFail "updateCachedMessageCount"

        let readBack =
            ThunderbirdStore.loadMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection ()
            |> okOrFail "loadMailAccounts"

        let updated = readBack |> List.find (fun a -> MailAccountId.value a.Id = @"C:\profile|account1")

        match updated.CachedMessageCount with
        | Some(count, storedAt) ->
            Assert.Equal(42, count)
            // LiteDB stores a DateTime as UTC and hands it back as DateTimeKind.Local, so the
            // INSTANT is what survives, not the ticks - the same LiteDB behaviour that forced the
            // watermark onto ModifiedAtTicksUtc. Here it is harmless and asserted as such: the
            // column is only ever displayed, never compared for equality.
            Assert.Equal(takenAt, storedAt.ToUniversalTime())
        | None -> Assert.Fail("Expected a cached message count after updateCachedMessageCount")

        // Everything else about the row survives the in-place update...
        Assert.Equal("Account C:\\profile|account1", updated.DisplayName)
        Assert.Equal(@"C:\profile\a1", updated.StoreDirectory)
        Assert.Equal<string list>([ @"C:\profile|account1@example.com" ], updated.EmailAddresses)
        Assert.Equal(Mbox, updated.StoreFormat)
        Assert.True updated.StoreDirectoryExists
        Assert.Equal(@"C:\profile", updated.ProfilePath)
        let folder = Assert.Single updated.Folders
        Assert.Equal("INBOX", folder.RelativePath)
        Assert.Equal(10L, folder.SizeBytes)

        // ...and the other account is untouched, so this really is an in-place update of one row
        // rather than saveMailAccounts' replace-everything.
        let other = readBack |> List.find (fun a -> MailAccountId.value a.Id = @"C:\profile|account2")
        Assert.Equal(None, other.CachedMessageCount)
        Assert.Equal(2, readBack.Length))

[<Fact; Trait("Level", "Integration")>]
let ``updateCachedMessageCount replaces a previous count rather than adding a second row`` () =
    withContext (fun context ->
        let id = MailAccountId.create @"C:\profile|account1" |> valueOrFail

        [ account @"C:\profile|account1" @"C:\profile\a1" ]
        |> ThunderbirdStore.saveMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection
        |> okOrFail "saveMailAccounts"

        ThunderbirdStore.updateCachedMessageCount handleError context.GetAccountsCollection id 1 (DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc))
        |> okOrFail "updateCachedMessageCount first"

        ThunderbirdStore.updateCachedMessageCount handleError context.GetAccountsCollection id 7 (DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc))
        |> okOrFail "updateCachedMessageCount second"

        let readBack =
            ThunderbirdStore.loadMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection ()
            |> okOrFail "loadMailAccounts"

        let only = Assert.Single readBack

        match only.CachedMessageCount with
        | Some(count, storedAt) ->
            Assert.Equal(7, count)
            Assert.Equal(DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc), storedAt.ToUniversalTime())
        | None -> Assert.Fail("Expected a cached message count"))

[<Fact; Trait("Level", "Integration")>]
let ``updateCachedMessageCount leaves the collection untouched when no account carries that id`` () =
    withContext (fun context ->
        let known = MailAccountId.create @"C:\profile|account1" |> valueOrFail
        let unknown = MailAccountId.create @"C:\profile|nobody" |> valueOrFail

        [ account @"C:\profile|account1" @"C:\profile\a1" ]
        |> ThunderbirdStore.saveMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection
        |> okOrFail "saveMailAccounts"

        // Not an error: MailAccountApiFactory only reaches here after countMessages has already
        // resolved the account, so an id with no row is a state the call cannot produce. What it
        // must not do is invent a row or fail the call the user was already told succeeded.
        ThunderbirdStore.updateCachedMessageCount handleError context.GetAccountsCollection unknown 42 DateTime.UtcNow
        |> okOrFail "updateCachedMessageCount"

        let readBack =
            ThunderbirdStore.loadMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection ()
            |> okOrFail "loadMailAccounts"

        let only = Assert.Single readBack
        Assert.Equal(MailAccountId.value known, MailAccountId.value only.Id)
        Assert.Equal(None, only.CachedMessageCount))

[<Fact; Trait("Level", "Unit")>]
let ``updateCachedMessageCount reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> AccountsCollection = fun () -> raise (InvalidOperationException "database is gone")
    let id = MailAccountId.create "a" |> valueOrFail

    let actual = ThunderbirdStore.updateCachedMessageCount recordingHandleError failingGetter id 42 DateTime.UtcNow

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.updateCachedMessageCount, ex.ActionName)
        Assert.Equal("Failed to update the cached message count.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- Selection ----------

[<Fact; Trait("Level", "Integration")>]
let ``loadSelectedMailAccount returns None for a fresh database`` () =
    withContext (fun context ->
        let actual =
            ThunderbirdStore.loadSelectedMailAccount handleError context.GetSelectedAccountCollection ()
            |> okOrFail "loadSelectedMailAccount"

        Assert.Equal(None, actual))

[<Fact; Trait("Level", "Integration")>]
let ``saveSelectedMailAccount then loadSelectedMailAccount round trips the selection`` () =
    withContext (fun context ->
        let id = MailAccountId.create @"C:\profile|account1" |> valueOrFail

        ThunderbirdStore.saveSelectedMailAccount handleError context.GetSelectedAccountCollection (Some id)
        |> okOrFail "saveSelectedMailAccount"

        let actual =
            ThunderbirdStore.loadSelectedMailAccount handleError context.GetSelectedAccountCollection ()
            |> okOrFail "loadSelectedMailAccount"

        Assert.Equal(Some id, actual))

[<Fact; Trait("Level", "Integration")>]
let ``saveSelectedMailAccount with None clears the selection, persisted as absent`` () =
    withContext (fun context ->
        let id = MailAccountId.create @"C:\profile|account1" |> valueOrFail

        ThunderbirdStore.saveSelectedMailAccount handleError context.GetSelectedAccountCollection (Some id)
        |> okOrFail "saveSelectedMailAccount set"
        |> ignore

        ThunderbirdStore.saveSelectedMailAccount handleError context.GetSelectedAccountCollection None
        |> okOrFail "saveSelectedMailAccount clear"
        |> ignore

        let actual =
            ThunderbirdStore.loadSelectedMailAccount handleError context.GetSelectedAccountCollection ()
            |> okOrFail "loadSelectedMailAccount"

        Assert.Equal(None, actual)
        Assert.Empty(context.GetSelectedAccountCollection().FindAll()))

// ---------- Watermarks ----------

[<Fact; Trait("Level", "Integration")>]
let ``loadWatermarkEntry returns None when no watermark has been saved`` () =
    withContext (fun context ->
        let id = MailAccountId.create @"C:\profile|account1" |> valueOrFail

        let actual =
            ThunderbirdStore.loadWatermarkEntry handleError context.GetWatermarksCollection id "INBOX"
            |> okOrFail "loadWatermarkEntry"

        Assert.Equal(None, actual))

[<Fact; Trait("Level", "Integration")>]
let ``saveWatermarkEntry then loadWatermarkEntry round trips the watermark`` () =
    withContext (fun context ->
        let id = MailAccountId.create @"C:\profile|account1" |> valueOrFail
        let watermark: FolderWatermark = { SizeBytes = 100L; ModifiedAt = DateTime(2026, 8, 20); OffsetReached = 50L }

        ThunderbirdStore.saveWatermarkEntry handleError context.GetWatermarksCollection id "INBOX" watermark
        |> okOrFail "saveWatermarkEntry"

        let actual =
            ThunderbirdStore.loadWatermarkEntry handleError context.GetWatermarksCollection id "INBOX"
            |> okOrFail "loadWatermarkEntry"

        Assert.Equal(Some watermark, actual))

[<Fact; Trait("Level", "Integration")>]
let ``saveWatermarkEntry updates an existing watermark rather than duplicating it`` () =
    withContext (fun context ->
        let id = MailAccountId.create @"C:\profile|account1" |> valueOrFail
        let first: FolderWatermark = { SizeBytes = 100L; ModifiedAt = DateTime(2026, 8, 20); OffsetReached = 50L }
        let second: FolderWatermark = { SizeBytes = 200L; ModifiedAt = DateTime(2026, 8, 21); OffsetReached = 150L }

        ThunderbirdStore.saveWatermarkEntry handleError context.GetWatermarksCollection id "INBOX" first
        |> okOrFail "saveWatermarkEntry first"
        |> ignore

        ThunderbirdStore.saveWatermarkEntry handleError context.GetWatermarksCollection id "INBOX" second
        |> okOrFail "saveWatermarkEntry second"
        |> ignore

        let all = context.GetWatermarksCollection().FindAll() |> Seq.toList
        let only = Assert.Single all
        Assert.Equal(200L, only.SizeBytes)

        let actual =
            ThunderbirdStore.loadWatermarkEntry handleError context.GetWatermarksCollection id "INBOX"
            |> okOrFail "loadWatermarkEntry"

        Assert.Equal(Some second, actual))

[<Fact; Trait("Level", "Integration")>]
let ``clearWatermarksFor deletes every watermark for the account and nothing else`` () =
    withContext (fun context ->
        let account1 = MailAccountId.create @"C:\profile|account1" |> valueOrFail
        let account2 = MailAccountId.create @"C:\profile|account2" |> valueOrFail
        let watermark: FolderWatermark = { SizeBytes = 1L; ModifiedAt = DateTime(2026, 8, 20); OffsetReached = 1L }

        ThunderbirdStore.saveWatermarkEntry handleError context.GetWatermarksCollection account1 "INBOX" watermark
        |> okOrFail "save 1a"
        |> ignore

        ThunderbirdStore.saveWatermarkEntry handleError context.GetWatermarksCollection account1 "Music" watermark
        |> okOrFail "save 1b"
        |> ignore

        ThunderbirdStore.saveWatermarkEntry handleError context.GetWatermarksCollection account2 "INBOX" watermark
        |> okOrFail "save 2"
        |> ignore

        ThunderbirdStore.clearWatermarksFor handleError context.GetWatermarksCollection account1
        |> okOrFail "clearWatermarksFor"

        let account1Watermark =
            ThunderbirdStore.loadWatermarkEntry handleError context.GetWatermarksCollection account1 "INBOX"
            |> okOrFail "loadWatermarkEntry account1"

        let account2Watermark =
            ThunderbirdStore.loadWatermarkEntry handleError context.GetWatermarksCollection account2 "INBOX"
            |> okOrFail "loadWatermarkEntry account2"

        Assert.Equal(None, account1Watermark)
        Assert.Equal(Some watermark, account2Watermark))

// ---------- Error paths (Unit - a failing getter, no real database involved) ----------

[<Fact; Trait("Level", "Unit")>]
let ``loadProfileRoot reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> ProfileRootCollection = fun () -> raise (InvalidOperationException "database is gone")

    let actual = ThunderbirdStore.loadProfileRoot recordingHandleError failingGetter ()

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadProfileRoot, ex.ActionName)
        Assert.Equal("Failed to load the profile root.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``saveProfileRoot reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> ProfileRootCollection = fun () -> raise (InvalidOperationException "database is gone")
    let path = ProfileRootPath.create @"C:\Thunderbird" |> valueOrFail

    let actual = ThunderbirdStore.saveProfileRoot recordingHandleError failingGetter path

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveProfileRoot, ex.ActionName)
        Assert.Equal("Failed to save the profile root.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``loadMailAccounts reports a MyDogsbodyException carrying its action when a collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> AccountsCollection = fun () -> raise (InvalidOperationException "database is gone")
    let foldersGetter: unit -> FoldersCollection = fun () -> raise (InvalidOperationException "unreachable")

    let actual = ThunderbirdStore.loadMailAccounts recordingHandleError failingGetter foldersGetter ()

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadMailAccounts, ex.ActionName)
        Assert.Equal("Failed to load mail accounts.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``saveMailAccounts reports a MyDogsbodyException carrying its action when a collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> AccountsCollection = fun () -> raise (InvalidOperationException "database is gone")
    let foldersGetter: unit -> FoldersCollection = fun () -> raise (InvalidOperationException "unreachable")

    let actual = ThunderbirdStore.saveMailAccounts recordingHandleError failingGetter foldersGetter []

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveMailAccounts, ex.ActionName)
        Assert.Equal("Failed to save mail accounts.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``loadSelectedMailAccount reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> SelectedAccountCollection = fun () -> raise (InvalidOperationException "database is gone")

    let actual = ThunderbirdStore.loadSelectedMailAccount recordingHandleError failingGetter ()

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadSelectedMailAccount, ex.ActionName)
        Assert.Equal("Failed to load the selected mail account.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``saveSelectedMailAccount reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> SelectedAccountCollection = fun () -> raise (InvalidOperationException "database is gone")

    let actual = ThunderbirdStore.saveSelectedMailAccount recordingHandleError failingGetter None

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveSelectedMailAccount, ex.ActionName)
        Assert.Equal("Failed to save the selected mail account.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``loadWatermarkEntry reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> WatermarksCollection = fun () -> raise (InvalidOperationException "database is gone")
    let id = MailAccountId.create "a" |> valueOrFail

    let actual = ThunderbirdStore.loadWatermarkEntry recordingHandleError failingGetter id "INBOX"

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadWatermark, ex.ActionName)
        Assert.Equal("Failed to load the folder watermark.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``saveWatermarkEntry reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> WatermarksCollection = fun () -> raise (InvalidOperationException "database is gone")
    let id = MailAccountId.create "a" |> valueOrFail
    let watermark: FolderWatermark = { SizeBytes = 1L; ModifiedAt = DateTime.UtcNow; OffsetReached = 1L }

    let actual = ThunderbirdStore.saveWatermarkEntry recordingHandleError failingGetter id "INBOX" watermark

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveWatermark, ex.ActionName)
        Assert.Equal("Failed to save the folder watermark.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``clearWatermarksFor reports a MyDogsbodyException carrying its action when the collection cannot be reached`` () =
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let failingGetter: unit -> WatermarksCollection = fun () -> raise (InvalidOperationException "database is gone")
    let id = MailAccountId.create "a" |> valueOrFail

    let actual = ThunderbirdStore.clearWatermarksFor recordingHandleError failingGetter id

    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.clearWatermarks, ex.ActionName)
        Assert.Equal("Failed to clear folder watermarks.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
        Assert.Single logged |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")
