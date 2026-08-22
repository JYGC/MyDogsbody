namespace MyDogsbody.UI.Types

type MailAccountUiType =
    {
        Id: string
        DisplayName: string
        EmailAddresses: string list
        StoreFormat: string
        StoreDirectory: string
        StoreDirectoryExists: bool
        Folders: MailFolderUiType list
        /// The count and when it was taken - a snapshot, not a live figure (design.md ->
        /// Decisions taken #4).
        CachedMessageCount: (int * System.DateTime) option
    }

type UnreadableDirectoryUiType =
    {
        Path: string
        Reason: string
    }

type DiscoveryResultUiType =
    {
        Accounts: MailAccountUiType list
        ProfilesFound: string list
        Unreadable: UnreadableDirectoryUiType list
    }
