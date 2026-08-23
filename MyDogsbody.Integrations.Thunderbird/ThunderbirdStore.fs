/// The integration's own facts: the profile root, discovered accounts and their folders, the
/// selected account, and scan watermarks.
///
/// Outer ring, so the shape is the established one - dependencies first, input last,
/// Result<'T, MyDogsbodyException> out, written with handleError. Domain error types are not
/// named here; translating between the two is the composition root's job and happens in
/// MailAccountApiFactory, nowhere else.
module MyDogsbody.Integrations.Thunderbird.ThunderbirdStore

open System
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird.Database.Types
open MyDogsbody.Integrations.Thunderbird.MailFolderReader

/// A row that cannot be mapped back is a data-integrity failure, not something a user did, so
/// it is raised and caught like any other unexpected failure rather than returned as a value.
let private valueOrRaise (result: Result<'T, string>) : 'T =
    match result with
    | Ok value -> value
    | Error reason -> raise (InvalidOperationException $"Stored value is unusable: {reason}")

// ---------- Profile root ----------

let loadProfileRoot
    (handleError: HandleErrorBuilder)
    (getProfileRootCollection: unit -> ProfileRootCollection)
    ()
    : Result<ProfileRootPath option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadProfileRoot

    handleError {
        try
            return
                getProfileRootCollection().FindAll()
                |> Seq.tryHead
                |> Option.map (ThunderbirdEntityMappers.toProfileRootPath >> valueOrRaise)
        with ex ->
            return! MyDogsbodyException(action, "Failed to load the profile root.", ex)
    }

/// One row: replaces whatever was stored before.
let saveProfileRoot
    (handleError: HandleErrorBuilder)
    (getProfileRootCollection: unit -> ProfileRootCollection)
    (path: ProfileRootPath)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveProfileRoot

    handleError {
        try
            let collection = getProfileRootCollection()
            collection.DeleteAll() |> ignore
            collection.Insert(ThunderbirdEntityMappers.toNewProfileRootEntity path) |> ignore
            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to save the profile root.", ex)
    }

// ---------- Accounts ----------

let loadMailAccounts
    (handleError: HandleErrorBuilder)
    (getAccountsCollection: unit -> AccountsCollection)
    (getFoldersCollection: unit -> FoldersCollection)
    ()
    : Result<DiscoveredMailAccount list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadMailAccounts

    handleError {
        try
            let accountEntities = getAccountsCollection().FindAll() |> Seq.toList
            let folderEntities = getFoldersCollection().FindAll() |> Seq.toList

            return
                accountEntities
                |> List.map (fun accountEntity ->
                    let folders =
                        folderEntities
                        |> List.filter (fun f -> f.AccountId = accountEntity.AccountId)
                        |> List.map ThunderbirdEntityMappers.toMailFolder

                    ThunderbirdEntityMappers.toDiscoveredMailAccount accountEntity folders |> valueOrRaise)
        with ex ->
            return! MyDogsbodyException(action, "Failed to load mail accounts.", ex)
    }

/// Replaces the previous set of accounts and folders rather than accumulating - a fresh scan's
/// result is the whole truth, not an addition to what was there before.
let saveMailAccounts
    (handleError: HandleErrorBuilder)
    (getAccountsCollection: unit -> AccountsCollection)
    (getFoldersCollection: unit -> FoldersCollection)
    (accounts: DiscoveredMailAccount list)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveMailAccounts

    handleError {
        try
            let accountsCollection = getAccountsCollection()
            let foldersCollection = getFoldersCollection()

            accountsCollection.DeleteAll() |> ignore
            foldersCollection.DeleteAll() |> ignore

            // Plain List.iter rather than `for...do` - HandleErrorBuilder has no Combine, and a
            // `for` loop inside a computation expression always desugars through it even when
            // it reads like an ordinary statement.
            accounts
            |> List.iter (fun account ->
                accountsCollection.Insert(ThunderbirdEntityMappers.toNewAccountEntity account) |> ignore
                let accountId = MailAccountId.value account.Id

                account.Folders
                |> List.iter (fun folder ->
                    foldersCollection.Insert(ThunderbirdEntityMappers.toNewFolderEntity accountId folder) |> ignore))

            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to save mail accounts.", ex)
    }

/// Updates one account's cached message count in place, leaving its folders untouched - unlike
/// saveMailAccounts, which replaces the whole set.
let updateCachedMessageCount
    (handleError: HandleErrorBuilder)
    (getAccountsCollection: unit -> AccountsCollection)
    (accountId: MailAccountId)
    (count: int)
    (takenAt: DateTime)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.updateCachedMessageCount

    handleError {
        try
            let accountIdValue = MailAccountId.value accountId
            let collection = getAccountsCollection()

            collection.FindAll()
            |> Seq.tryFind (fun a -> a.AccountId = accountIdValue)
            |> Option.iter (fun entity ->
                entity.CachedMessageCount <- Nullable count
                entity.CachedMessageCountTakenAt <- Nullable takenAt
                collection.Update entity |> ignore)

            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to update the cached message count.", ex)
    }

// ---------- Selection ----------

let loadSelectedMailAccount
    (handleError: HandleErrorBuilder)
    (getSelectedAccountCollection: unit -> SelectedAccountCollection)
    ()
    : Result<MailAccountId option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadSelectedMailAccount

    handleError {
        try
            return
                getSelectedAccountCollection().FindAll()
                |> Seq.tryHead
                |> ThunderbirdEntityMappers.toSelectedMailAccountId
                |> valueOrRaise
        with ex ->
            return! MyDogsbodyException(action, "Failed to load the selected mail account.", ex)
    }

/// One row: `None` clears the selection - persisted as absent, not as an empty string, so
/// reading it back is never ambiguous with "an account whose id happens to be empty".
let saveSelectedMailAccount
    (handleError: HandleErrorBuilder)
    (getSelectedAccountCollection: unit -> SelectedAccountCollection)
    (selected: MailAccountId option)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveSelectedMailAccount

    handleError {
        try
            let collection = getSelectedAccountCollection()
            collection.DeleteAll() |> ignore

            // Plain Option.iter rather than `match` - see the List.iter comment in
            // saveMailAccounts for why: HandleErrorBuilder has no Combine.
            selected
            |> Option.iter (fun id -> collection.Insert(ThunderbirdEntityMappers.toNewSelectedAccountEntity id) |> ignore)

            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to save the selected mail account.", ex)
    }

// ---------- Watermarks ----------

let loadWatermarkEntry
    (handleError: HandleErrorBuilder)
    (getWatermarksCollection: unit -> WatermarksCollection)
    (accountId: MailAccountId)
    (relativePath: string)
    : Result<FolderWatermark option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadWatermark

    handleError {
        try
            let accountIdValue = MailAccountId.value accountId

            return
                getWatermarksCollection().FindAll()
                |> Seq.tryFind (fun w -> w.AccountId = accountIdValue && w.RelativePath = relativePath)
                |> Option.map ThunderbirdEntityMappers.toFolderWatermark
        with ex ->
            return! MyDogsbodyException(action, "Failed to load the folder watermark.", ex)
    }

let saveWatermarkEntry
    (handleError: HandleErrorBuilder)
    (getWatermarksCollection: unit -> WatermarksCollection)
    (accountId: MailAccountId)
    (relativePath: string)
    (watermark: FolderWatermark)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveWatermark

    handleError {
        try
            let accountIdValue = MailAccountId.value accountId
            let collection = getWatermarksCollection()

            let existing =
                collection.FindAll()
                |> Seq.tryFind (fun w -> w.AccountId = accountIdValue && w.RelativePath = relativePath)

            // A plain function, called either way, rather than `match` used as a mid-CE
            // statement - see the List.iter comment in saveMailAccounts for why.
            let persist () =
                match existing with
                | Some existingEntity ->
                    let updated = ThunderbirdEntityMappers.toNewWatermarkEntity accountIdValue relativePath watermark
                    updated.Id <- existingEntity.Id
                    collection.Update updated |> ignore
                | None ->
                    collection.Insert(ThunderbirdEntityMappers.toNewWatermarkEntity accountIdValue relativePath watermark)
                    |> ignore

            persist ()
            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to save the folder watermark.", ex)
    }

/// Deletes every watermark for one account, forcing every folder to be re-read in full on the
/// next scan.
let clearWatermarksFor
    (handleError: HandleErrorBuilder)
    (getWatermarksCollection: unit -> WatermarksCollection)
    (accountId: MailAccountId)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.clearWatermarks

    handleError {
        try
            let accountIdValue = MailAccountId.value accountId
            getWatermarksCollection().DeleteMany(fun w -> w.AccountId = accountIdValue) |> ignore
            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to clear folder watermarks.", ex)
    }
