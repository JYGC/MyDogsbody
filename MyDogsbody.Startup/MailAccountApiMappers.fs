/// The top mapping point: domain type <-> MyDogsbody.UI.Types record, plus the translation
/// between the two error types.
///
/// Total functions with no module-level bindings, so a test reaches them without Startup.fs
/// opening a database.
module MyDogsbody.Startup.MailAccountApiMappers

open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.UI.Types

/// A UI-facing string, independent of ThunderbirdEntityMappers' persisted-value convention -
/// this is a display concern, not a storage one, even though the two happen to agree today.
let private toStoreFormatUiString (format: StoreFormat) : string =
    match format with
    | Mbox -> "Mbox"
    | Maildir -> "Maildir"

let toMailFolderUiType (folder: MailFolder) : MailFolderUiType =
    {
        RelativePath = folder.RelativePath
        DisplayName = folder.DisplayName
        SizeBytes = folder.SizeBytes
        IsScannable = folder.IsScannable
    }

let toMailAccountUiType (account: DiscoveredMailAccount) : MailAccountUiType =
    {
        Id = MailAccountId.value account.Id
        DisplayName = account.DisplayName
        EmailAddresses = account.EmailAddresses
        StoreFormat = toStoreFormatUiString account.StoreFormat
        StoreDirectory = account.StoreDirectory
        StoreDirectoryExists = account.StoreDirectoryExists
        Folders = account.Folders |> List.map toMailFolderUiType
        CachedMessageCount = account.CachedMessageCount
    }

let toUnreadableDirectoryUiType (unreadable: UnreadableDirectory) : UnreadableDirectoryUiType =
    { Path = unreadable.Path; Reason = unreadable.Reason }

let toDiscoveryResultUiType (result: DiscoveryResult) : DiscoveryResultUiType =
    {
        Accounts = result.Accounts |> List.map toMailAccountUiType
        ProfilesFound = result.ProfilesFound
        Unreadable = result.Unreadable |> List.map toUnreadableDirectoryUiType
        SelectionCleared = result.SelectionCleared
    }

/// Inbound: an adapter's exception becomes the one domain case that stands for infrastructure
/// failure. The adapter's own handleError has already logged it, so nothing logs again here.
let toMailAccountError (ex: MyDogsbodyException) : MailAccountError = MailStoreFailed ex.Message

/// Outbound: a domain error case becomes the exception the UI renders as a sentence.
///
/// Nothing here logs, in either branch - this value is constructed inside Result.mapError and
/// returned straight to the UI, never inside a handleError block. `MailFolderUnreadable` and
/// `ProfileUnreadable` are expected (design.md -> Error-handling approach): a locked or
/// malformed file is a fact about the machine, not a defect worth a stack trace, so they are
/// marked with an ApplicationException the same way the always-expected cases are.
/// `MailStoreFailed` is the one case that genuinely was an exception, so it is left unmarked -
/// its adapter has already logged it once via handleError.
let toMyDogsbodyException (action: string) (error: MailAccountError) : MyDogsbodyException =
    let expected (message: string) = MyDogsbodyException(action, message, System.ApplicationException message)

    match error with
    | ProfileRootInvalid reason -> expected reason
    | ProfileRootMissing -> expected "No Thunderbird profile folder has been chosen yet."
    | ProfileRootUnreachable(path, reason) ->
        expected $"The chosen Thunderbird profile folder '{path}' could not be reached: {reason}"
    | NoProfileFound searchedPath -> expected $"No Thunderbird profile was found under '{searchedPath}'."
    | ProfileUnreadable(path, reason) -> expected $"Could not read the profile at '{path}': {reason}"
    | MailAccountIdInvalid reason -> expected reason
    | MailAccountNotFound id -> expected $"No mail account was found with id '{MailAccountId.value id}'."
    | NoAccountSelected -> expected "No mail account has been selected yet."
    | StoreDirectoryMissing(id, path) ->
        expected $"The store directory for account '{MailAccountId.value id}' does not exist: '{path}'."
    | MailFolderUnreadable(folder, reason) -> expected $"Could not read the folder '{folder}': {reason}"
    | MailStoreFailed message -> MyDogsbodyException(action, message)
