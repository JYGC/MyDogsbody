module MyDogsbody.Tests.Contracts.ThunderbirdPersistedShapeTests

open System
open System.IO
open Xunit
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Integrations.Thunderbird.Database.Types
open MyDogsbody.Integrations.Thunderbird.MailFolderReader

// LiteDB is schemaless, so renaming a property on any of the five entities silently orphans
// every row already stored - the code keeps compiling and the old data simply stops being
// found. These assert the persisted document's field names, not just that an object round
// trips.

let private handleError = HandleErrorBuilder(fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error(ex: MyDogsbody.Exceptions.Types.MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

/// A second handle on the same file, so the raw documents can be inspected as stored.
let private withStoreAndRawAccess (test: ThunderbirdDatabaseContext -> LiteDatabase -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "shared"
    use rawDatabase = new LiteDatabase($"Filename={databasePath};connection=shared")

    try
        test context rawDatabase
    finally
        context.Dispose()

        try
            File.Delete databasePath
        with _ ->
            ()

[<Fact; Trait("Level", "Contract")>]
let ``a profile root is persisted under the documented field name`` () =
    withStoreAndRawAccess (fun context rawDatabase ->
        let path = ProfileRootPath.create @"C:\Thunderbird" |> valueOrFail
        ThunderbirdStore.saveProfileRoot handleError context.GetProfileRootCollection path |> okOrFail "saveProfileRoot"

        let document = rawDatabase.GetCollection("ProfileRoot").FindAll() |> Seq.exactlyOne
        Assert.True(document.ContainsKey "_id", "expected an _id field")
        Assert.True(document.ContainsKey "Path", "expected a Path field")
        Assert.Equal(@"C:\Thunderbird", document.["Path"].AsString))

[<Fact; Trait("Level", "Contract")>]
let ``an account is persisted under the documented field names`` () =
    withStoreAndRawAccess (fun context rawDatabase ->
        let account: DiscoveredMailAccount =
            {
                Id = MailAccountId.create @"C:\p|account1" |> valueOrFail
                ProfilePath = @"C:\p"
                DisplayName = "Alpha Mail"
                EmailAddresses = [ "alice@alpha.example.com" ]
                StoreFormat = Mbox
                StoreDirectory = @"C:\p\a1"
                StoreDirectoryExists = true
                Folders = []
                CachedMessageCount = Some(4, DateTime(2026, 8, 20))
            }

        ThunderbirdStore.saveMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection [ account ]
        |> okOrFail "saveMailAccounts"

        let document = rawDatabase.GetCollection("Accounts").FindAll() |> Seq.exactlyOne

        for field in
            [ "_id"
              "AccountId"
              "ProfilePath"
              "DisplayName"
              "EmailAddresses"
              "StoreFormat"
              "StoreDirectory"
              "StoreDirectoryExists"
              "CachedMessageCount"
              "CachedMessageCountTakenAt" ] do
            Assert.True(document.ContainsKey field, $"expected a {field} field")

        Assert.Equal(@"C:\p|account1", document.["AccountId"].AsString)
        Assert.Equal("Mbox", document.["StoreFormat"].AsString)
        Assert.False(document.ContainsKey "Folders", "Folders must not be persisted on the account row - they live in the Folders collection"))

[<Fact; Trait("Level", "Contract")>]
let ``a folder is persisted under the documented field names, in the Folders collection`` () =
    withStoreAndRawAccess (fun context rawDatabase ->
        let account: DiscoveredMailAccount =
            {
                Id = MailAccountId.create @"C:\p|account1" |> valueOrFail
                ProfilePath = @"C:\p"
                DisplayName = "Alpha Mail"
                EmailAddresses = []
                StoreFormat = Mbox
                StoreDirectory = @"C:\p\a1"
                StoreDirectoryExists = true
                Folders = [ { RelativePath = "INBOX"; DisplayName = "INBOX"; SizeBytes = 42L; IsScannable = true } ]
                CachedMessageCount = None
            }

        ThunderbirdStore.saveMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection [ account ]
        |> okOrFail "saveMailAccounts"

        let document = rawDatabase.GetCollection("Folders").FindAll() |> Seq.exactlyOne

        for field in [ "_id"; "AccountId"; "RelativePath"; "DisplayName"; "SizeBytes"; "IsScannable" ] do
            Assert.True(document.ContainsKey field, $"expected a {field} field")

        Assert.Equal(@"C:\p|account1", document.["AccountId"].AsString)
        Assert.Equal("INBOX", document.["RelativePath"].AsString)
        Assert.Equal(42L, document.["SizeBytes"].AsInt64))

[<Fact; Trait("Level", "Contract")>]
let ``the selected account is persisted under the documented field name`` () =
    withStoreAndRawAccess (fun context rawDatabase ->
        let id = MailAccountId.create @"C:\p|account1" |> valueOrFail

        ThunderbirdStore.saveSelectedMailAccount handleError context.GetSelectedAccountCollection (Some id)
        |> okOrFail "saveSelectedMailAccount"

        let document = rawDatabase.GetCollection("SelectedAccount").FindAll() |> Seq.exactlyOne
        Assert.True(document.ContainsKey "AccountId", "expected an AccountId field")
        Assert.Equal(@"C:\p|account1", document.["AccountId"].AsString))

[<Fact; Trait("Level", "Contract")>]
let ``a watermark is persisted under the documented field names`` () =
    withStoreAndRawAccess (fun context rawDatabase ->
        let id = MailAccountId.create @"C:\p|account1" |> valueOrFail
        let modifiedAt = DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc).AddTicks 1234L
        let watermark: FolderWatermark = { SizeBytes = 100L; ModifiedAt = modifiedAt; OffsetReached = 50L }

        ThunderbirdStore.saveWatermarkEntry handleError context.GetWatermarksCollection id "INBOX" watermark
        |> okOrFail "saveWatermarkEntry"

        let document = rawDatabase.GetCollection("Watermarks").FindAll() |> Seq.exactlyOne

        // `ModifiedAt` is deliberately gone, renamed to `ModifiedAtTicksUtc` and stored as an
        // int64: LiteDB truncates a DateTime to whole milliseconds and returns it as Local, and
        // readFolder's watermark check compares the loaded value to the file's own mtime with
        // `=`. Asserting the field is an Int64 is what keeps that from quietly reverting.
        for field in [ "_id"; "AccountId"; "RelativePath"; "SizeBytes"; "ModifiedAtTicksUtc"; "OffsetReached" ] do
            Assert.True(document.ContainsKey field, $"expected a {field} field")

        Assert.False(document.ContainsKey "ModifiedAt", "ModifiedAt was renamed to ModifiedAtTicksUtc")

        Assert.Equal(@"C:\p|account1", document.["AccountId"].AsString)
        Assert.Equal("INBOX", document.["RelativePath"].AsString)
        Assert.Equal(100L, document.["SizeBytes"].AsInt64)
        Assert.Equal(modifiedAt.Ticks, document.["ModifiedAtTicksUtc"].AsInt64)
        Assert.Equal(50L, document.["OffsetReached"].AsInt64))

[<Fact; Trait("Level", "Contract")>]
let ``the five collections are named exactly as documented, and only those five exist`` () =
    withStoreAndRawAccess (fun context rawDatabase ->
        let id = MailAccountId.create @"C:\p|account1" |> valueOrFail
        let path = ProfileRootPath.create @"C:\p" |> valueOrFail

        ThunderbirdStore.saveProfileRoot handleError context.GetProfileRootCollection path |> okOrFail "saveProfileRoot"

        ThunderbirdStore.saveMailAccounts
            handleError
            context.GetAccountsCollection
            context.GetFoldersCollection
            [
                {
                    Id = id
                    ProfilePath = @"C:\p"
                    DisplayName = "d"
                    EmailAddresses = []
                    StoreFormat = Mbox
                    StoreDirectory = @"C:\p\a1"
                    StoreDirectoryExists = true
                    Folders = [ { RelativePath = "INBOX"; DisplayName = "INBOX"; SizeBytes = 1L; IsScannable = true } ]
                    CachedMessageCount = None
                }
            ]
        |> okOrFail "saveMailAccounts"

        ThunderbirdStore.saveSelectedMailAccount handleError context.GetSelectedAccountCollection (Some id)
        |> okOrFail "saveSelectedMailAccount"

        ThunderbirdStore.saveWatermarkEntry
            handleError
            context.GetWatermarksCollection
            id
            "INBOX"
            { SizeBytes = 1L; ModifiedAt = DateTime.UtcNow; OffsetReached = 1L }
        |> okOrFail "saveWatermarkEntry"

        Assert.Equal<string list>(
            [ "Accounts"; "Folders"; "ProfileRoot"; "SelectedAccount"; "Watermarks" ] |> List.sort,
            rawDatabase.GetCollectionNames() |> List.ofSeq |> List.sort
        ))
