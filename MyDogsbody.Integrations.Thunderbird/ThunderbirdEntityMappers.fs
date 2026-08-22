/// The bottom mapper: LiteDB entity (C#) ⇄ domain type / integration-internal type. Nothing
/// upstream of this file ever constructs an entity directly.
module MyDogsbody.Integrations.Thunderbird.ThunderbirdEntityMappers

open System
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird.Database.Models
open MyDogsbody.Integrations.Thunderbird.MailFolderReader

let storeFormatToString (format: StoreFormat) : string =
    match format with
    | Mbox -> "Mbox"
    | Maildir -> "Maildir"

/// An unrecognised stored value is an error, not a silently-chosen default - a corrupted or
/// hand-edited row must not be read back as if it were a real account's format.
let storeFormatOfString (raw: string) : Result<StoreFormat, string> =
    match raw with
    | "Mbox" -> Ok Mbox
    | "Maildir" -> Ok Maildir
    | other -> Error $"Unrecognised store format: {other}"

// ---------- ThunderbirdProfileRoot ⇄ ProfileRootPath ----------

let toNewProfileRootEntity (path: ProfileRootPath) : ThunderbirdProfileRoot =
    ThunderbirdProfileRoot(Path = ProfileRootPath.value path)

let toProfileRootPath (entity: ThunderbirdProfileRoot) : Result<ProfileRootPath, string> =
    ProfileRootPath.create entity.Path

// ---------- DiscoveredFolderEntity ⇄ MailFolder ----------

let toNewFolderEntity (accountId: string) (folder: MailFolder) : DiscoveredFolderEntity =
    DiscoveredFolderEntity(
        AccountId = accountId,
        RelativePath = folder.RelativePath,
        DisplayName = folder.DisplayName,
        SizeBytes = folder.SizeBytes,
        IsScannable = folder.IsScannable
    )

let toMailFolder (entity: DiscoveredFolderEntity) : MailFolder =
    {
        RelativePath = entity.RelativePath
        DisplayName = entity.DisplayName
        SizeBytes = entity.SizeBytes
        IsScannable = entity.IsScannable
    }

// ---------- DiscoveredAccountEntity ⇄ DiscoveredMailAccount ----------
//
// The account entity does not carry its folders - they live in the separate Folders
// collection, keyed by AccountId, and are supplied here by the caller (ThunderbirdStore, which
// joins the two collections when loading).

let toNewAccountEntity (account: DiscoveredMailAccount) : DiscoveredAccountEntity =
    let cachedCount, cachedAt =
        match account.CachedMessageCount with
        | Some(count, takenAt) -> Nullable count, Nullable takenAt
        | None -> Nullable(), Nullable()

    DiscoveredAccountEntity(
        AccountId = MailAccountId.value account.Id,
        ProfilePath = account.ProfilePath,
        DisplayName = account.DisplayName,
        EmailAddresses = ResizeArray account.EmailAddresses,
        StoreFormat = storeFormatToString account.StoreFormat,
        StoreDirectory = account.StoreDirectory,
        StoreDirectoryExists = account.StoreDirectoryExists,
        CachedMessageCount = cachedCount,
        CachedMessageCountTakenAt = cachedAt
    )

let toDiscoveredMailAccount (entity: DiscoveredAccountEntity) (folders: MailFolder list) : Result<DiscoveredMailAccount, string> =
    match MailAccountId.create entity.AccountId, storeFormatOfString entity.StoreFormat with
    | Error reason, _ -> Error reason
    | _, Error reason -> Error reason
    | Ok id, Ok format ->
        let cachedMessageCount =
            match Option.ofNullable entity.CachedMessageCount, Option.ofNullable entity.CachedMessageCountTakenAt with
            | Some count, Some takenAt -> Some(count, takenAt)
            | _ -> None

        Ok
            {
                Id = id
                ProfilePath = entity.ProfilePath
                DisplayName = entity.DisplayName
                EmailAddresses = List.ofSeq entity.EmailAddresses
                StoreFormat = format
                StoreDirectory = entity.StoreDirectory
                StoreDirectoryExists = entity.StoreDirectoryExists
                Folders = folders
                CachedMessageCount = cachedMessageCount
            }

// ---------- SelectedAccountEntity ⇄ MailAccountId option ----------

let toNewSelectedAccountEntity (id: MailAccountId) : SelectedAccountEntity =
    SelectedAccountEntity(AccountId = MailAccountId.value id)

let toSelectedMailAccountId (entity: SelectedAccountEntity option) : Result<MailAccountId option, string> =
    match entity with
    | None -> Ok None
    | Some e ->
        match MailAccountId.create e.AccountId with
        | Ok id -> Ok(Some id)
        | Error reason -> Error reason

// ---------- ScanWatermarkEntity ⇄ FolderWatermark ----------

let toNewWatermarkEntity (accountId: string) (relativePath: string) (watermark: FolderWatermark) : ScanWatermarkEntity =
    ScanWatermarkEntity(
        AccountId = accountId,
        RelativePath = relativePath,
        SizeBytes = watermark.SizeBytes,
        ModifiedAt = watermark.ModifiedAt,
        OffsetReached = watermark.OffsetReached
    )

let toFolderWatermark (entity: ScanWatermarkEntity) : FolderWatermark =
    {
        SizeBytes = entity.SizeBytes
        ModifiedAt = entity.ModifiedAt
        OffsetReached = entity.OffsetReached
    }
