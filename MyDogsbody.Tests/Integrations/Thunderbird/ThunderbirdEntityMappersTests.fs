module MyDogsbody.Tests.Integrations.Thunderbird.ThunderbirdEntityMappersTests

open System
open Xunit
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Integrations.Thunderbird.Database.Models
open MyDogsbody.Integrations.Thunderbird.MailFolderReader

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

// ---------- StoreFormat ----------

[<Fact; Trait("Level", "Unit")>]
let ``storeFormatToString and storeFormatOfString round trip Mbox`` () =
    Assert.Equal("Mbox", ThunderbirdEntityMappers.storeFormatToString Mbox)
    Assert.Equal(Ok Mbox, ThunderbirdEntityMappers.storeFormatOfString "Mbox")

[<Fact; Trait("Level", "Unit")>]
let ``storeFormatToString and storeFormatOfString round trip Maildir`` () =
    Assert.Equal("Maildir", ThunderbirdEntityMappers.storeFormatToString Maildir)
    Assert.Equal(Ok Maildir, ThunderbirdEntityMappers.storeFormatOfString "Maildir")

[<Fact; Trait("Level", "Unit")>]
let ``storeFormatOfString rejects an unrecognised value rather than defaulting`` () =
    let actual = ThunderbirdEntityMappers.storeFormatOfString "Pop3Store"

    match actual with
    | Error reason -> Assert.Equal("Unrecognised store format: Pop3Store", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- ThunderbirdProfileRoot ⇄ ProfileRootPath ----------

[<Fact; Trait("Level", "Unit")>]
let ``toNewProfileRootEntity and toProfileRootPath round trip every field`` () =
    let path = ProfileRootPath.create @"C:\Users\test\Thunderbird" |> valueOrFail

    let entity = ThunderbirdEntityMappers.toNewProfileRootEntity path
    Assert.Equal(@"C:\Users\test\Thunderbird", entity.Path)

    let roundTripped = ThunderbirdEntityMappers.toProfileRootPath entity |> valueOrFail
    Assert.Equal(@"C:\Users\test\Thunderbird", ProfileRootPath.value roundTripped)

// ---------- DiscoveredFolderEntity ⇄ MailFolder ----------

[<Fact; Trait("Level", "Unit")>]
let ``toNewFolderEntity and toMailFolder round trip every field`` () =
    let folder: MailFolder =
        {
            RelativePath = "Music/Surrey Hills Orchestra"
            DisplayName = "Surrey Hills Orchestra"
            SizeBytes = 12345L
            IsScannable = true
        }

    let entity = ThunderbirdEntityMappers.toNewFolderEntity "account1" folder
    Assert.Equal("account1", entity.AccountId)
    Assert.Equal("Music/Surrey Hills Orchestra", entity.RelativePath)
    Assert.Equal("Surrey Hills Orchestra", entity.DisplayName)
    Assert.Equal(12345L, entity.SizeBytes)
    Assert.True entity.IsScannable

    let roundTripped = ThunderbirdEntityMappers.toMailFolder entity
    Assert.Equal(folder, roundTripped)

// ---------- DiscoveredAccountEntity ⇄ DiscoveredMailAccount ----------

let private account : DiscoveredMailAccount =
    {
        Id = MailAccountId.create @"C:\profile|account1" |> valueOrFail
        ProfilePath = @"C:\profile"
        DisplayName = "Alpha Mail"
        EmailAddresses = [ "alice@alpha.example.com"; "alice2@alpha.example.com" ]
        StoreFormat = Mbox
        StoreDirectory = @"C:\profile\ImapMail\imap.alpha.example.com"
        StoreDirectoryExists = true
        Folders = []
        CachedMessageCount = Some(4, DateTime(2026, 8, 20, 10, 0, 0))
    }

[<Fact; Trait("Level", "Unit")>]
let ``toNewAccountEntity and toDiscoveredMailAccount round trip every field including a cached count`` () =
    let entity = ThunderbirdEntityMappers.toNewAccountEntity account

    Assert.Equal(@"C:\profile|account1", entity.AccountId)
    Assert.Equal(@"C:\profile", entity.ProfilePath)
    Assert.Equal("Alpha Mail", entity.DisplayName)
    Assert.Equal<string list>([ "alice@alpha.example.com"; "alice2@alpha.example.com" ], List.ofSeq entity.EmailAddresses)
    Assert.Equal("Mbox", entity.StoreFormat)
    Assert.Equal(@"C:\profile\ImapMail\imap.alpha.example.com", entity.StoreDirectory)
    Assert.True entity.StoreDirectoryExists
    Assert.Equal(Nullable 4, entity.CachedMessageCount)
    Assert.Equal(Nullable(DateTime(2026, 8, 20, 10, 0, 0)), entity.CachedMessageCountTakenAt)

    let roundTripped = ThunderbirdEntityMappers.toDiscoveredMailAccount entity [] |> valueOrFail
    Assert.Equal(account, roundTripped)

[<Fact; Trait("Level", "Unit")>]
let ``toDiscoveredMailAccount carries the folders it is given, separately from the entity`` () =
    let entity = ThunderbirdEntityMappers.toNewAccountEntity { account with CachedMessageCount = None }

    let folders =
        [
            { RelativePath = "INBOX"; DisplayName = "INBOX"; SizeBytes = 10L; IsScannable = true }
        ]

    let roundTripped = ThunderbirdEntityMappers.toDiscoveredMailAccount entity folders |> valueOrFail
    Assert.Equal<MailFolder list>(folders, roundTripped.Folders)
    Assert.Equal(None, roundTripped.CachedMessageCount)

[<Fact; Trait("Level", "Unit")>]
let ``toDiscoveredMailAccount rejects an unrecognised stored store format`` () =
    let entity = ThunderbirdEntityMappers.toNewAccountEntity account
    entity.StoreFormat <- "Pop3Store"

    let actual = ThunderbirdEntityMappers.toDiscoveredMailAccount entity []

    match actual with
    | Error reason -> Assert.Equal("Unrecognised store format: Pop3Store", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- SelectedAccountEntity ⇄ MailAccountId option ----------

[<Fact; Trait("Level", "Unit")>]
let ``toNewSelectedAccountEntity and toSelectedMailAccountId round trip a selection`` () =
    let id = MailAccountId.create @"C:\profile|account1" |> valueOrFail

    let entity = ThunderbirdEntityMappers.toNewSelectedAccountEntity id
    Assert.Equal(@"C:\profile|account1", entity.AccountId)

    let roundTripped = ThunderbirdEntityMappers.toSelectedMailAccountId (Some entity) |> valueOrFail
    Assert.Equal(Some id, roundTripped)

[<Fact; Trait("Level", "Unit")>]
let ``toSelectedMailAccountId maps an absent row to None`` () =
    let actual = ThunderbirdEntityMappers.toSelectedMailAccountId None |> valueOrFail
    Assert.Equal(None, actual)

// ---------- ScanWatermarkEntity ⇄ FolderWatermark ----------

[<Fact; Trait("Level", "Unit")>]
let ``toNewWatermarkEntity and toFolderWatermark round trip every field`` () =
    let watermark: FolderWatermark =
        {
            SizeBytes = 4096L
            ModifiedAt = DateTime(2026, 8, 20, 9, 30, 0)
            OffsetReached = 2048L
        }

    let entity = ThunderbirdEntityMappers.toNewWatermarkEntity "account1" "INBOX" watermark
    Assert.Equal("account1", entity.AccountId)
    Assert.Equal("INBOX", entity.RelativePath)
    Assert.Equal(4096L, entity.SizeBytes)
    Assert.Equal(DateTime(2026, 8, 20, 9, 30, 0), entity.ModifiedAt)
    Assert.Equal(2048L, entity.OffsetReached)

    let roundTripped = ThunderbirdEntityMappers.toFolderWatermark entity
    Assert.Equal(watermark, roundTripped)
