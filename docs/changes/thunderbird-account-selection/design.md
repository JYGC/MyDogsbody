# Design — Thunderbird account selection

Change **#3 of 7**. Requirements in [`requirements.md`](requirements.md); decision record and the
measured profile in
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md).

---

## What makes this change different from the others

Everything else in the series builds on shapes the codebase already has. This one builds an
integration from nothing, against a file format with no schema, on a **16 GB** store that another
process is writing to while we read it.

Three things that would ordinarily be design decisions are already settled by measurement, and
getting any of them wrong is not a style problem:

| Would-be decision | Settled by the measurement |
| --- | --- |
| Discover accounts structurally or from `prefs.js`? | **`prefs.js`.** A directory walk finds 15 accounts where 9 exist — deleted accounts leave their directories behind, one with 2 GB in it |
| Resolve the store path from `directory` or `directory-rel`? | **`directory-rel`.** The absolute value on the real profile points at `C:\Users\JunYing\...`, a user that was renamed years ago |
| Copy the store and parse it, or read it in place? | **Read in place.** A single mbox is 2.5 GB. There is no fallback to argue about |

---

## System architecture and components

```
 MyDogsbody (C# WPF host)
   MainWindow / DI ── FolderPicker = fun () -> Microsoft.Win32.OpenFolderDialog …
        │                                    ← the FIRST host change in the series
        ▼
 UI.Portal  /settings/mail-accounts
   MailAccountsPage.fs ─ MailAccountsComponents.fs ─ MailAccountsBrowserModuleCreators.fs
        ▼
 UI.Types   MailAccountApi { GetProfileRoot; SetProfileRoot; ScanForAccounts;
                             GetAccounts; SelectAccount; CountMessages; ClearWatermarks }
            FolderPicker  (a function type the host satisfies — not a domain dependency)
        ▼
 Startup    MailAccountApiFactory.fs · MailAccountApiMappers.fs
        ▼
 Domain     MailAccounts/MailAccountsTypes.fs
            MailAccounts/SetProfileRootWorkflow.fs
            MailAccounts/ScanForMailAccountsWorkflow.fs
            MailAccounts/ListMailAccountsWorkflow.fs
            MailAccounts/SelectMailAccountWorkflow.fs
        ▲
 Integrations.Thunderbird
   ThunderbirdFolderScanner.fs     recursive prefs.js walk, depth- and loop-bounded
   ThunderbirdAccountReader.fs     prefs.js → accounts, identities, store format, directory-rel
   MailFolderEnumerator.fs         mbox (.sbd nesting) and maildir (cur/new/tmp), .msf ignored
   MailFolderReader.fs             MimeKit, FileShare.ReadWrite, cutoff, watermarks
   ThunderbirdStore.fs             the integration's own facts
   Database/ThunderbirdDatabaseContextModule.fs   + BsonMapper warm-up per entity
   ThunderbirdEntityMappers.fs     entity ⇄ domain  (the BOTTOM mapper)
        ▲
 Integrations.Thunderbird.Database.Models (C#)
   ThunderbirdProfileRoot · DiscoveredAccount · DiscoveredFolder · SelectedAccount · ScanWatermark
```

### Projects added

| Project | Holds |
| --- | --- |
| `MyDogsbody.Integrations.Thunderbird` (F#, net9.0) | The five adapter files, the LiteDB context and the entity mapper. References `MyDogsbody.Domain`, `MyDogsbody.Builders`, `MyDogsbody.Exceptions.Types` and its own `.Database.Models`, and nothing else |
| `MyDogsbody.Integrations.Thunderbird.Database.Models` (C#, net9.0) | Five mutable entity classes. LiteDB needs settable properties and `ObjectId` |

New package: **MimeKit** in the integration only. `MyDogsbody.Domain` gains nothing.

### The domain area, and why it is its own

`MailAccounts/` is a new workflow area rather than part of `Invoices/`, because **this change depends
on nothing** — it is one of the three independent starting points, and putting its types in
`Invoices/` would make it wait for change #2. It also reads correctly on its own terms: a mail
account is not an invoice concern.

`ScanCutoff` lives here too, not in `Invoices/`. A cutoff is a parameter *to a mail read*, and the
rule it encodes — *skip a message on its `Date` header before touching its body* — is the reader's
rule. Change #4's `ScanWindowDays` computes one and hands it over. **This is a deviation from the
pre-proposal's placement, taken so this change stays independent.**

---

## Data models and interfaces

### `Domain/MailAccounts/MailAccountsTypes.fs`

```fsharp
namespace MyDogsbody.Domain.MailAccounts

type ProfileRootPath = private ProfileRootPath of string
module ProfileRootPath =
    let create (value: string) : Result<ProfileRootPath, string> = …   // non-empty; rooted
    let value (ProfileRootPath p) = p

type MailAccountId = private MailAccountId of string
module MailAccountId = …

/// How the account's messages are stored on disk. From storeContractID, never guessed.
type StoreFormat = Mbox | Maildir

/// The instant a read stops at. A distinct type from any window or duration on purpose: a window
/// is a CHOICE, a cutoff is a FACT derived from a clock, and the adapter must be handed the
/// second so it cannot re-derive it differently. Anchored to the start of a day - Q1.18.
type ScanCutoff = private ScanCutoff of System.DateTime
module ScanCutoff =
    let ofStartOfDay (instant: System.DateTime) = ScanCutoff instant.Date
    let value (ScanCutoff c) = c

type MailFolder =
    { RelativePath: string        // "Inbox", "Music.sbd/Surrey Hills Orchestra.sbd/Messages"
      DisplayName: string
      SizeBytes: int64
      IsScannable: bool }         // false for Trash/Deleted/Junk/Sent/Drafts - Q4.8

/// What discovery found. "Configured but missing" is a state, not an omission.
type DiscoveredMailAccount =
    { Id: MailAccountId
      ProfilePath: string          // qualifies duplicates across profiles - Q4.9
      DisplayName: string
      EmailAddresses: string list  // an account can have several identities
      StoreFormat: StoreFormat
      StoreDirectory: string
      StoreDirectoryExists: bool
      Folders: MailFolder list
      CachedMessageCount: (int * System.DateTime) option }

/// A directory the walk could not read. Reported, never fatal - friction #13.
type UnreadableDirectory = { Path: string; Reason: string }

type DiscoveryResult =
    { Accounts: DiscoveredMailAccount list
      ProfilesFound: string list
      Unreadable: UnreadableDirectory list }

type MailAttachment =
    { FileName: string
      DeclaredContentType: string   // kept, but NEVER used to choose a reader - see change #4
      Content: byte[] }             // bytes, not a path - Q1.11

type MailMessage =
    { SourceMessageId: string       // plain string here; change #4 constrains it
      Sender: string
      Subject: string
      ReceivedAt: System.DateTime
      BodyText: string option
      BodyHtml: string option       // both returned; the reader downstream chooses - Finding 5
      Attachments: MailAttachment list }

type MailAccountError =
    | ProfileRootInvalid      of reason: string
    | ProfileRootMissing
    | NoProfileFound          of searchedPath: string
    | ProfileUnreadable       of path: string * reason: string
    | MailAccountNotFound     of MailAccountId
    | NoAccountSelected
    | StoreDirectoryMissing   of MailAccountId * path: string
    | MailFolderUnreadable    of folder: string * reason: string
    | MailStoreFailed         of message: string

// Dependencies. The domain names no path parser, no ILiteCollection and no MIME type.
type DiscoverMailAccounts  = ProfileRootPath -> Result<DiscoveryResult, MailAccountError>
type LoadProfileRoot       = unit -> Result<ProfileRootPath option, MailAccountError>
type SaveProfileRoot       = ProfileRootPath -> Result<unit, MailAccountError>
type LoadMailAccounts      = unit -> Result<DiscoveredMailAccount list, MailAccountError>
type SaveMailAccounts      = DiscoveredMailAccount list -> Result<unit, MailAccountError>
type LoadSelectedMailAccount = unit -> Result<MailAccountId option, MailAccountError>
type SaveSelectedMailAccount = MailAccountId option -> Result<unit, MailAccountError>
type CountMessages         = MailAccountId -> Result<int, MailAccountError>
type ClearWatermarks       = MailAccountId -> Result<unit, MailAccountError>

/// The one change #4 consumes. Declared here with its adapter, so it has a real implementation
/// and a contract suite from the day it exists.
type ReadMailFolder = MailAccountId -> ScanCutoff -> Result<MailMessage list, MailAccountError>
```

### Workflows

| File | Signature | Does |
| --- | --- | --- |
| `SetProfileRootWorkflow.fs` | `SaveProfileRoot -> string -> Result<ProfileRootPath, MailAccountError>` | Validates the path, persists it |
| `ScanForMailAccountsWorkflow.fs` | `LoadProfileRoot -> DiscoverMailAccounts -> SaveMailAccounts -> LoadSelectedMailAccount -> SaveSelectedMailAccount -> unit -> Result<DiscoveryResult, MailAccountError>` | Discovers, stores, and **reconciles the selection** — if the selected account is gone, the selection is cleared |
| `ListMailAccountsWorkflow.fs` | `LoadMailAccounts -> LoadSelectedMailAccount -> unit -> Result<DiscoveredMailAccount list * MailAccountId option, MailAccountError>` | Reads back what was stored |
| `SelectMailAccountWorkflow.fs` | `LoadMailAccounts -> SaveSelectedMailAccount -> string -> Result<MailAccountId, MailAccountError>` | Refuses an id not among the stored accounts |

Selection reconciliation is a **workflow** rule rather than an adapter one so it is unit-tested with
lambdas: a stale selection pointing at a deleted account is exactly the state nobody tests by hand.

### The folder picker is not a domain dependency

```fsharp
// MyDogsbody.UI.Types
type FolderPicker = unit -> string option
```

The domain never opens dialogs, so `FolderPicker` is a **UI-level service**, registered by the WPF
host and injected into the page with `html.inject`. Production binds
`Microsoft.Win32.OpenFolderDialog`; a test binds `fun () -> Some fixturePath`. The page takes the
path it gets and calls `MailAccountApi.SetProfileRoot`.

This keeps friction #12 to the smallest possible host change: one function and one registration.
Blazor cannot supply a filesystem path at all — `InputFile` hands over content, not locations, and
`webkitdirectory` is no better inside a `BlazorWebView` — so the seam has to be here.

### LiteDB entities (C#)

| Entity | Collection | Holds |
| --- | --- | --- |
| `ThunderbirdProfileRoot` | `ProfileRoot` | One row: the chosen folder |
| `DiscoveredAccountEntity` | `Accounts` | Per account: profile path, display name, identities, store format, store directory, exists flag, cached count and when |
| `DiscoveredFolderEntity` | `Folders` | Per folder: account id, relative path, display name, size, scannable flag |
| `SelectedAccountEntity` | `SelectedAccount` | One row: the selected account id |
| `ScanWatermarkEntity` | `Watermarks` | Per folder: account id, relative path, file size, mtime, offset reached, last scanned |

`ThunderbirdDatabaseContextModule.getDatabaseContext databasePath connectionType` returns a record of
five getters plus `Dispose`, and calls `BsonMapper.Global.ToDocument(TheEntity()) |> ignore` for
**every one of the five** before returning. That warm-up is not optional: LiteDB builds entity
mappings lazily on a global mapper, and two threads mapping the same entity for the first time at
once can observe a half-built mapping and silently drop a property. It was a 6-in-10 intermittent
failure when it was missed once already, and a scan running on a background thread is exactly the
reachable case.

---

## Sequence diagrams

### Discovery — the measured algorithm

```
ThunderbirdFolderScanner.discover rootPath
   │
   ├─ walk rootPath recursively for prefs.js
   │    · depth bound (12)
   │    · canonical-path set, so a junction cannot loop           ← friction #13
   │    · UnauthorizedAccess / IO per directory → Unreadable, CONTINUE
   │
   └─ for each prefs.js found → ThunderbirdAccountReader.read profileDir
        │
        ├─ mail.accountmanager.accounts   →  ["account1"; "account2"; …]   ← AUTHORITATIVE
        │     NOT a directory listing (would find 15 where 9 exist)
        │     NOT 1..lastKey (lastKey is 20, numbering has gaps)
        │
        ├─ per account key:
        │     mail.account.<k>.server      → "server3"
        │     mail.account.<k>.identities  → ["id10"; "id11"]     ← can be several
        │
        ├─ per server:
        │     type, hostname, userName, name
        │     storeContractID → berkeleystore = Mbox | maildirstore = Maildir
        │     directory-rel   → "[ProfD]ImapMail/imap.googlemail-1.com"
        │        └─ [ProfD] resolves against THE FOLDER THE USER CHOSE
        │           (the absolute `directory` value is stale on the real profile)
        │     resolved directory missing? → StoreDirectoryExists = false, KEEP the account
        │
        ├─ per identity: useremail, fullName
        │
        └─ MailFolderEnumerator.enumerate storeDir format
              mbox:    extensionless file = a folder;  sibling X.sbd = its children; recurse
              maildir: a directory holding cur/ new/ tmp = a folder
              .msf:    IGNORED ENTIRELY (Mork; the real profile has .msf with no mbox)
              Trash/Deleted/Junk/Sent/Drafts → IsScannable = false   (9.0 of 15.2 GB)
```

### Reading a folder — in place, cutoff-first, incremental

```
MailFolderReader.read accountId cutoff
   │
   ├─ for each SCANNABLE folder, in size order (smallest first, so the page fills early)
   │
   ├─ watermark lookup
   │     size == recorded && mtime == recorded  →  seek to recorded offset   (incremental)
   │     size < recorded, or mtime inconsistent →  DISCARD watermark, read whole folder
   │                                                (a compact or repair invalidates the offset)
   │
   ├─ open  FileMode.Open, FileAccess.Read, FileShare.ReadWrite        ← Thunderbird is running
   │        NEVER copied. NEVER written. 16 GB, one file of 2.5 GB.
   │
   ├─ per message:
   │     parse HEADERS only
   │     Date header older than cutoff?  → SKIP without touching body or attachments   ← Q1.6
   │                                        (this is what makes a 7-day window cheap)
   │     Date missing or unparseable?    → INCLUDE (skipping would be silent data loss)
   │     otherwise → parse body + attachments with MimeKit, attachments as BYTES
   │
   ├─ final message torn (Thunderbird mid-write)? → DISCARD it, return the rest
   ├─ folder locked / unreadable?                 → MailFolderUnreadable, CONTINUE other folders
   │
   └─ record watermark: size, mtime, offset reached
```

### Choosing a folder — the one host change

```
MailAccountsPage        FolderPicker (WPF)      MailAccountApi        ScanForMailAccountsWorkflow
     │  Browse              │                        │                          │
     ├─────────────────────►│ OpenFolderDialog       │                          │
     │◄─ Some path ─────────┤                        │                          │
     ├─ SetProfileRoot path ───────────────────────► │ validate + persist       │
     ├─ ScanForAccounts ───────────────────────────► ├─────────────────────────►│
     │                                               │                          ├ discover
     │                                               │                          ├ save accounts
     │                                               │                          ├ selected account
     │                                               │                          │   still present?
     │                                               │                          │   no → clear it
     │◄─ DiscoveryResult (accounts, profiles, unreadable) ───────────────────────┤
     └─ transact: table, unreadable list, alert cleared
```

---

## Error-handling approach

| Ring | Type | Builder |
| --- | --- | --- |
| `Domain/MailAccounts` | `MailAccountError` | `result` |
| `Integrations.Thunderbird`, composition root | `MyDogsbodyException` | `handleError` |

Meeting once, in `MailAccountApiFactory`. Inbound, an adapter exception becomes `MailStoreFailed`;
outbound, a `MailAccountError` becomes a `MyDogsbodyException` carrying the API operation's action.

**Which failures are expected**, and therefore wrap an `ApplicationException` and pass through
`handleError` unlogged:

| Case | Expected? |
| --- | --- |
| `ProfileRootInvalid`, `ProfileRootMissing`, `NoProfileFound`, `MailAccountNotFound`, `NoAccountSelected`, `StoreDirectoryMissing` | **Yes** — the user pointed at the wrong place, or has not chosen yet |
| `ProfileUnreadable`, `MailFolderUnreadable` | **Yes** — a locked or permission-denied file is a fact about the machine, reported on screen, not a defect worth a stack trace |
| `MailStoreFailed` | **No** — logged once |

`MailFolderUnreadable` being *expected* is the deliberate part: Thunderbird is running by
assumption (Q4.4), so a locked file is normal operation. Logging one row per locked folder per scan
would fill the log with the expected case and bury the real ones.

### Action names

```
ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdFolderScanner.discover
                                              .ThunderbirdAccountReader.read
                                              .MailFolderEnumerator.enumerate
                                              .MailFolderReader.read / countMessages
                                              .ThunderbirdStore.{load,save}ProfileRoot
                                                               .{load,save}Accounts
                                                               .{load,save}SelectedAccount
                                                               .{load,save,clear}Watermarks
ActionNames.MyDogsbody.Startup.MailAccountApi.*
```

The structural suite requires every string to end with the name of the binding that declares it and
no two bindings to share one.

---

## Testing strategy

### Fixtures — the whole basis of this change's credibility

`Fixtures/ThunderbirdProfiles/` holds **committed synthetic profiles**. A test that depends on one
machine's mailbox is not a test, and the real profile is 16 GB and full of personal mail.

`measured-shape/` reproduces every trap the real profile contained:

| Shape | What it catches |
| --- | --- |
| 10 accounts in `mail.accountmanager.accounts`, `lastKey = 20`, gaps at 4, 5, 7, 8, 11–16 | Iterating `1..lastKey` |
| 15 directories under `ImapMail/`, 6 with no account pointing at them | Structural discovery |
| Numeric-infix directory names (`imap.example.com`, `-1`, `-2`, `-3`) | Treating a directory name as a hostname or an account |
| `directory` pointing at a nonexistent user path, `directory-rel` correct | Using the absolute path |
| One account whose resolved store directory is absent | Dropping it silently instead of reporting it |
| One account with two identities | Assuming one address per account |
| `.sbd` nesting three levels deep | A one-level folder walk |
| `Archives.msf` and `Drafts.msf` with no mbox | Trusting `.msf` as an index |
| `Trash`, `Junk`, `Sent`, `Drafts` present and sized | Forgetting the exclusions |

**The acceptance test for discovery** is that scanning `measured-shape/` finds exactly the accounts
`prefs.js` declares — not the directories, not the orphans.

Reader fixtures: an mbox with a `From `-quoted body line; an mbox whose final message is truncated
mid-headers; a message with no `Message-ID`; a message with an unparseable `Date`; a message with
both `text/plain` and `text/html`; a PDF attachment declared `application/octet-stream`; an
attachment with no filename. Plus `maildir-shape/`, which is **synthetic only** — the measured
profile is 100% mbox, so there is nothing real to verify maildir against, and that limitation is
stated in the change description rather than implied.

### Unit

Every `create`, one rule at a time with the reason asserted. Every workflow's Ok path with all
fields, and every error case with its payload. **Dependency-not-called**: `SelectMailAccountWorkflow`
given an unknown id must not call `saveSelectedMailAccount`. **Selection reconciliation**: a stored
selection naming an account absent from a fresh discovery is cleared, and the workflow says so.

### Integration

Adapters against real files in a fresh temp directory per test. The LiteDB store against a fresh temp
database, `connection=direct`, disposed before delete, **with the delete asserted**. Reader tests
against the fixtures above, including: a cutoff skips older messages *without* parsing their bodies
(asserted by a fixture whose old messages carry deliberately malformed MIME that would throw if
parsed); a watermark makes a second read return only appended messages; a shrunk file discards the
watermark and re-reads; a locked file reports `MailFolderUnreadable` and the other folders still
return.

### Contract

One shared suite per dependency function type — all eleven, including `ReadMailFolder` — run against
the real adapter **and** every fake used in a workflow unit test. `MemberData` sources are **public**
`let`s. The entity mapper field-for-field both directions, and a **persisted-shape** test asserting
the stored document's field names for each of the five entities, because LiteDB is schemaless and a
renamed property silently orphans stored data.

### E2E

Choose a folder → the path is shown; scan → accounts appear; select one → it is shown selected and
persists across a reload; a walk hitting an unreadable directory → the directory and its reason are
listed and the accounts still appear; a failure → `MudAlert`, cleared by the next success. The folder
picker is a lambda, so no window opens.

### Manual verification — required, and recorded

The suite cannot exercise a real 16 GB profile or a running Thunderbird. **Before this change is
called done**, run the app against the real profile with Thunderbird open and record in
`outcome.md`:

- the number of accounts found (expected: the number `prefs.js` declares, not the directory count),
- how long a full folder enumeration took,
- how long a headers-only message count took for the largest account,
- that Thunderbird noticed nothing and the profile was unmodified.

The third number is what friction #14 and Q1.9 are riding on, and change #4 needs it before
rescan-on-every-click can be treated as settled.

---

## Decisions taken

1. **`MailAccounts/` is its own domain area, and `ScanCutoff` lives in it.** Keeps this change
   independent of #1 and #2 as §4 requires, and puts the cutoff beside the reader whose rule it is.
2. **`MailFolderReader` lands here, not in change #4.** The integration is complete when it can hand
   over messages and attachments; #4 binds `ReadMailFolder` as a dependency. It is not dead code
   here — the message count on the accounts page runs the same header pass.
3. **`FolderPicker` is a UI service, not a domain dependency.** The domain does not open dialogs. One
   function type, one host registration, one lambda in tests.
4. **Message count is user-triggered and cached, not shown on page load.** The measurement puts a
   header pass over the real profile at *minutes*. Rendering a table cannot cost that. The count is
   an action, the result is cached with its timestamp, and the page says when it was taken.
5. **`SourceMessageId` is a plain string in this change.** The constrained type lives in `Invoices/`
   (change #2). Keeping it a string here is what keeps this change independent; #4's mapper
   constrains it.
6. **A synthesised message id is a hash of the message's headers, not a byte offset.** An offset
   changes when Thunderbird compacts a folder, which would orphan every problem row recorded against
   that message. The header hash survives compaction.
7. **Both body alternatives are returned.** Finding 5 says HTML is the better source where the body
   matters, but *which* alternative to prefer is the document reader's decision (change #4), not the
   mail reader's. Returning both means that decision can change without touching this integration.
8. **`DeclaredContentType` is carried but never used to choose a reader.** 155 of 644 PDFs arrive as
   `application/octet-stream`. It is kept because an attachment with no filename needs *something* to
   name it in an unsupported-format report.
9. **`MailFolderUnreadable` is an expected failure and is not logged.** Thunderbird is running by
   assumption; a locked folder is normal. Logging it per folder per scan would bury the real errors.
10. **Folders are read smallest-first.** The page fills with results early instead of stalling on a
    2.5 GB file, and a user who has seen enough can stop.

---

## Risks

| Risk | Handling |
| --- | --- |
| **Friction #14 — 16 GB is a design input.** Any performance assumption checked against a test mailbox is worthless | Read in place, cutoff applied on headers, exclusions applied before reading, incremental after the first pass. The manual verification records real numbers |
| **Friction #3 — Thunderbird is writing while we read** | `FileShare.ReadWrite`, a torn final message discarded, a locked folder reported and skipped. Never write, never copy, never lock — and an integration test that asserts the fixture files are byte-identical after a read |
| **Friction #13 — walking a folder the user chose** | Depth bound, canonical-path visited set against junction loops, per-directory permission errors reported rather than aborting, and "no accounts here" distinguished from "I could not look" |
| **Friction #4 — `.msf` is Mork** | Ignored entirely. `global-messages-db.sqlite` (gloda) would be faster but is not guaranteed enabled or current; recorded as a possible optimisation, not built |
| **Friction #12 — the first host change** | One function type, one WPF registration, one lambda in tests. Nothing else in `MainWindow.xaml.cs` moves |
| **Maildir ships unverified against real data** | Synthetic fixtures only, and **said plainly in the change description** rather than implied. The measured profile is 100% mbox; there is no real maildir to test against |
| **The LiteDB global mapper race** | `BsonMapper.Global.ToDocument` warm-up for all five entities before the context returns. This was a 6-in-10 intermittent failure once already |
| **A synthesised message id changes and orphans problem rows** | Header hash, not byte offset. Asserted by a fixture that is compacted between two reads |
| **mbox parsing is subtly wrong** (`From ` quoting, encodings, nested multipart) | MimeKit rather than hand-rolled parsing, plus fixtures for each known trap |
