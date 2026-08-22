namespace MyDogsbody.UI.Types

type MailAccountUiType =
    {
        Id: string
        /// The profile this account was discovered in. Carried to the screen rather than stopping
        /// at the domain, because requirements.md asks for accounts to be listed "qualified by the
        /// profile path they came from" - the chosen folder may hold a live profile AND a backup
        /// copy of it, and without this two rows for the same account render identically while the
        /// user is being asked to pick one of them for import.
        ProfilePath: string
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
        /// This scan found the previously selected account gone and cleared the selection, so the
        /// page can say so rather than just letting the tick vanish.
        SelectionCleared: bool
    }
