namespace MyDogsbody.Domain.MailAccounts

// The mail accounts workflow area: constrained primitives, one type per pipeline stage, the
// area's error DU, and the dependency function types its workflows declare.
//
// This area depends on nothing else in the invoice-to-calendar series - see design.md -> "The
// domain area, and why it is its own". Nothing here names ILiteCollection, a prefs.js key, an
// mbox offset or a MIME type. The domain cannot reach any of them, and does not need to.

/// The folder the user chose to search for Thunderbird profiles. Validated non-empty and
/// rooted so [ProfD] always has somewhere real to resolve against.
type ProfileRootPath = private ProfileRootPath of string

module ProfileRootPath =

    let create (value: string) : Result<ProfileRootPath, string> =
        let trimmed = if isNull value then "" else value.Trim()

        if System.String.IsNullOrWhiteSpace trimmed then
            Error "Profile root path must not be empty."
        elif not (System.IO.Path.IsPathRooted trimmed) then
            Error "Profile root path must be an absolute path."
        else
            Ok (ProfileRootPath trimmed)

    let value (ProfileRootPath p) = p

/// The identifier the store assigned. Opaque to the domain, the same way SupplierId is.
type MailAccountId = private MailAccountId of string

module MailAccountId =

    let create (value: string) : Result<MailAccountId, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Mail account id must not be empty."
        else
            Ok (MailAccountId value)

    let value (MailAccountId id) = id

/// How the account's messages are stored on disk. From storeContractID, never guessed.
type StoreFormat =
    | Mbox
    | Maildir

/// The instant a read stops at. A distinct type from any window or duration on purpose: a
/// window is a CHOICE, a cutoff is a FACT derived from a clock, and the adapter must be handed
/// the second so it cannot re-derive one differently. Anchored to the start of a day - Q1.18.
type ScanCutoff = private ScanCutoff of System.DateTime

module ScanCutoff =

    let ofStartOfDay (instant: System.DateTime) : ScanCutoff = ScanCutoff instant.Date

    let value (ScanCutoff c) = c

/// One folder inside an account's store.
type MailFolder =
    {
        /// "Inbox", "Music.sbd/Surrey Hills Orchestra.sbd/Messages"
        RelativePath: string
        DisplayName: string
        SizeBytes: int64
        /// False for Trash/Deleted/Junk/Sent/Drafts - Q4.8.
        IsScannable: bool
    }

/// What discovery found for one account. "Configured but missing" is a state, not an omission.
type DiscoveredMailAccount =
    {
        Id: MailAccountId
        /// Qualifies duplicates across profiles - Q4.9.
        ProfilePath: string
        DisplayName: string
        /// An account can have several identities.
        EmailAddresses: string list
        StoreFormat: StoreFormat
        StoreDirectory: string
        StoreDirectoryExists: bool
        Folders: MailFolder list
        CachedMessageCount: (int * System.DateTime) option
    }

/// A directory the walk could not read. Reported, never fatal - friction #13.
type UnreadableDirectory = { Path: string; Reason: string }

type DiscoveryResult =
    {
        Accounts: DiscoveredMailAccount list
        ProfilesFound: string list
        Unreadable: UnreadableDirectory list
        /// True when this scan found the previously selected account gone and cleared the
        /// selection. requirements.md -> "Selecting an account" asks the system to clear it **and
        /// say so**, and design.md's Unit section spells out the same thing ("...is cleared, and
        /// the workflow says so"). Without a value on the way out there is nothing for the page to
        /// say it with: the tick simply vanishes and the user is never told why the account they
        /// chose is no longer chosen. A plain bool rather than the id that went away - the account
        /// is gone, so its id names nothing the user can act on, and the workflow already reports
        /// what IS there in `Accounts`.
        SelectionCleared: bool
    }

type MailAttachment =
    {
        FileName: string
        /// Kept, but NEVER used to choose a reader - see change #4.
        DeclaredContentType: string
        /// Bytes, not a path - Q1.11.
        Content: byte[]
    }

type MailMessage =
    {
        SourceMessageId: string
        Sender: string
        Subject: string
        ReceivedAt: System.DateTime
        BodyText: string option
        /// Both alternatives are returned; the reader downstream chooses - Finding 5.
        BodyHtml: string option
        Attachments: MailAttachment list
    }

/// What can go wrong in this area, in terms a person could say out loud. Each case carries the
/// values its message is written from.
///
/// MailAccountIdInvalid is not in design.md's original listing - the design shows
/// SelectMailAccountWorkflow taking a raw string but its error DU has no case for a malformed
/// (empty) id, only for one that parses but names no known account. Adding one here is the
/// smallest fix, the same shape as SupplierError's PaymentTermInvalid addition.
type MailAccountError =
    | ProfileRootInvalid of reason: string
    | ProfileRootMissing
    /// A folder WAS chosen, but it cannot be reached now - deleted, moved, or on a drive or
    /// network share that has gone away. Distinct from NoProfileFound on purpose: "the folder is
    /// gone" and "the folder is there but holds no profile" need different answers from the
    /// user, and the stored path is kept either way so they can see what it was.
    | ProfileRootUnreachable of path: string * reason: string
    | NoProfileFound of searchedPath: string
    | ProfileUnreadable of path: string * reason: string
    | MailAccountIdInvalid of reason: string
    | MailAccountNotFound of MailAccountId
    | NoAccountSelected
    | StoreDirectoryMissing of MailAccountId * path: string
    | MailFolderUnreadable of folder: string * reason: string
    | MailStoreFailed of message: string

// Dependencies as function types - not interfaces, not classes, not a collection getter. A
// workflow receives a function value, so a test supplies a lambda and the composition root
// supplies the real adapter. The domain names no path parser, no ILiteCollection and no MIME
// type.

type DiscoverMailAccounts = ProfileRootPath -> Result<DiscoveryResult, MailAccountError>
type LoadProfileRoot = unit -> Result<ProfileRootPath option, MailAccountError>
type SaveProfileRoot = ProfileRootPath -> Result<unit, MailAccountError>
type LoadMailAccounts = unit -> Result<DiscoveredMailAccount list, MailAccountError>
type SaveMailAccounts = DiscoveredMailAccount list -> Result<unit, MailAccountError>
type LoadSelectedMailAccount = unit -> Result<MailAccountId option, MailAccountError>
type SaveSelectedMailAccount = MailAccountId option -> Result<unit, MailAccountError>
type CountMessages = MailAccountId -> Result<int, MailAccountError>
type ClearWatermarks = MailAccountId -> Result<unit, MailAccountError>

/// The one change #4 consumes. Declared here with its adapter, so it has a real implementation
/// and a contract suite from the day it exists.
type ReadMailFolder = MailAccountId -> ScanCutoff -> Result<MailMessage list, MailAccountError>
