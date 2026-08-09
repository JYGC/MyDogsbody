# Requirements — Thunderbird account selection

Change **#3 of 7**. Depends on nothing — one of the three independent starting points. See
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md) for the decision
record; question ids (`Q4.2`), the measured profile, and friction numbers (#3, #4, #12, #13, #14)
resolve there.

**What this change is for.** There is nothing in the repository today that reads Thunderbird — no
profile discovery, no mbox or maildir reader, no MIME parser. This change builds the whole
integration: find the accounts, list their folders, let one be selected, and read messages out of it.
Ask #5.

**What makes it unusually well specified for a from-scratch integration.** The plan was measured
against a real 16 GB profile before it was written, and three assumptions did not survive: structural
account detection, absolute store paths, and copy-then-parse. Every requirement below marked with a
number is a count from that profile, not a guess.

**What it is not.** No invoices, no templates, no extraction, no calendar. This change hands over
messages and attachments; it does not know invoices exist.

---

## Choosing the profile folder

WHEN a user opens the mail accounts page and no profile folder has been chosen THE SYSTEM SHALL say so explicitly and invite the user to choose one, rather than showing an empty account table.
WHEN a user presses Browse THE SYSTEM SHALL open the operating system's native folder dialog.
WHEN a user cancels the folder dialog THE SYSTEM SHALL leave the current setting unchanged.
WHEN a user chooses a folder THE SYSTEM SHALL persist that path in the Thunderbird integration's own store, and SHALL NOT write it to the main database.
WHEN the folder picker is provided THE SYSTEM SHALL declare it as a function type satisfied by the WPF host in production and by a lambda in tests.
WHEN a previously chosen folder no longer exists THE SYSTEM SHALL report that specifically, and SHALL keep the stored path so the user can see what it was.

---

## Discovering accounts

### Walking the chosen folder

WHEN a folder is chosen THE SYSTEM SHALL search it **recursively** for `prefs.js` files, treating each one found as a profile root (Q4.2).
WHEN the chosen folder is itself one profile, the parent of several, or a backup copy THE SYSTEM SHALL handle all three without configuration.
WHEN a directory cannot be read THE SYSTEM SHALL record that directory and its reason and **continue the walk**, rather than aborting.
WHEN the walk completes THE SYSTEM SHALL distinguish "no accounts were found here" from "some directories could not be read", because the two need different answers from the user.
WHEN the walk descends THE SYSTEM SHALL stop at a bounded depth, so an unexpectedly large tree cannot run indefinitely.
WHEN the walk encounters a directory it has already visited by another path THE SYSTEM SHALL NOT descend into it again, so a Windows junction cannot produce a loop.
WHEN several profiles are found THE SYSTEM SHALL list all of their accounts, qualified by the profile path they came from, so two profiles containing the same account are distinguishable (Q4.9).

### Reading the profile

WHEN a profile is read THE SYSTEM SHALL take the account list from `mail.accountmanager.accounts` and SHALL NOT infer accounts from the directory structure — on the measured profile a directory walk finds **15** accounts where **9** exist, because deleted accounts leave their directories behind.
WHEN a profile is read THE SYSTEM SHALL NOT iterate account keys from 1 to `mail.account.lastKey` — on the measured profile `lastKey` is 20, there are 10 accounts, and the numbering has gaps.
WHEN a mail directory has no account pointing at it THE SYSTEM SHALL ignore it.
WHEN an account's store directory is resolved THE SYSTEM SHALL use `mail.server.<server>.directory-rel` and resolve `[ProfD]` against **the folder the user chose** — the absolute `directory` value was measurably stale on the real profile and points at a user path that no longer exists.
WHEN an account is read THE SYSTEM SHALL take its type, hostname, username, display name and store format from its `mail.server.<server>.*` entries.
WHEN an account is read THE SYSTEM SHALL take every identity's email address and full name from its `mail.identity.<id>.*` entries, because an account can have more than one.
WHEN an account's resolved store directory does not exist THE SYSTEM SHALL report it as configured-but-missing rather than dropping it silently.
WHEN `storeContractID` is `berkeleystore` THE SYSTEM SHALL treat the account as mbox; WHEN it is `maildirstore` THE SYSTEM SHALL treat it as maildir.

### Listing folders

WHEN an mbox account's folders are enumerated THE SYSTEM SHALL treat an extensionless file as a folder's mbox and a sibling `.sbd` directory as its children, repeating to arbitrary depth.
WHEN a maildir account's folders are enumerated THE SYSTEM SHALL treat a directory containing `cur`, `new` and `tmp` as a folder.
WHEN folders are enumerated THE SYSTEM SHALL ignore `.msf` files entirely — they are Mork format and are not a reliable index of what exists; the measured profile has `Archives.msf` and `Drafts.msf` with no corresponding mbox file at all (friction #4).
WHEN folders are enumerated THE SYSTEM SHALL exclude `Trash`, `Deleted`, `Junk`, `Sent` and `Drafts` from the scannable set (Q4.8) — on the measured profile that removes **9.0 GB of 15.2 GB**, which is the difference between a feasible scan and an infeasible one.
WHEN folders are enumerated THE SYSTEM SHALL record each folder's on-disk size, so the page can state what a scan will cost before it is run.

---

## Selecting an account

WHEN the mail accounts page loads THE SYSTEM SHALL display every discovered account with its display name, its email addresses, its store format, its folder count and its on-disk size.
WHEN a user selects an account THE SYSTEM SHALL persist the selection in the Thunderbird integration's own store (Q4.1) and SHALL NOT write it to the main database.
WHEN the page loads and an account is already selected THE SYSTEM SHALL show it as selected.
WHEN the selected account no longer appears in a fresh discovery THE SYSTEM SHALL clear the selection and say so, rather than leaving a selection pointing at nothing.
WHEN no account has been selected THE SYSTEM SHALL report that state distinctly, so a later scan can refuse with a reason rather than silently finding nothing.
WHEN a user requests a message count for an account THE SYSTEM SHALL run a headers-only pass, report the count, and cache it with the time it was taken.
WHEN a cached message count is displayed THE SYSTEM SHALL state when it was taken, because the count is a snapshot and the mailbox keeps growing.

---

## Reading mail

### Reading safely while Thunderbird is running

WHEN mail is read THE SYSTEM SHALL open the store **for reading in place**, with sharing that permits Thunderbird's own reads and writes, and SHALL NEVER copy the store — the measured profile is **16 GB** with a single 2.5 GB mbox file (friction #14).
WHEN mail is read THE SYSTEM SHALL NEVER write to, move, truncate or lock any file in the profile. The integration is read-only by construction.
WHEN the final message in an mbox is torn because Thunderbird is mid-write THE SYSTEM SHALL discard that partial message and return everything before it, rather than failing the folder.
WHEN a file cannot be opened because it is locked THE SYSTEM SHALL report that folder as unreadable with a reason a user can act on, and SHALL continue with the other folders (friction #3).

### The cutoff

WHEN a folder is read THE SYSTEM SHALL be given a cutoff and SHALL apply it while reading, rather than reading everything and discarding afterwards.
WHEN a message's `Date` header is older than the cutoff THE SYSTEM SHALL skip it **before** parsing its body or attachments — that is what makes a short window cheap over a large store (Q1.6).
WHEN a message's `Date` header is missing or unparseable THE SYSTEM SHALL include the message rather than skip it, because excluding it would be silent data loss with nothing on screen to show for it.
WHEN a cutoff is constructed THE SYSTEM SHALL make it a distinct type from any window or duration, so an adapter is handed a computed instant and cannot re-derive one differently.

### Incremental scanning

WHEN a folder is read THE SYSTEM SHALL record a watermark for it — the file size and modification time at the time of reading, and the offset reached.
WHEN a folder is read again and its watermark still matches the file THE SYSTEM SHALL read only what has been appended since (Q4.10).
WHEN a folder's size has shrunk, or its modification time is inconsistent with its watermark THE SYSTEM SHALL discard the watermark and read the whole folder, because a compact or a repair invalidates the offset.
WHEN a user requests a full rescan THE SYSTEM SHALL clear every watermark for that account.
WHEN watermarks are stored THE SYSTEM SHALL keep them in the Thunderbird integration's own store.

### What a message yields

WHEN a message is read THE SYSTEM SHALL return its identifier, sender, subject, received date, body and attachments.
WHEN a message has a `Message-ID` header THE SYSTEM SHALL use it as the identifier.
WHEN a message has no `Message-ID` THE SYSTEM SHALL synthesise a **stable** identifier that does not change when the folder is compacted, so a problem recorded against a message can still be found later.
WHEN a message carries attachments THE SYSTEM SHALL return each attachment's filename and its **bytes**, and SHALL NOT write it to a temporary file (Q1.11).
WHEN a message carries both a `text/plain` and a `text/html` alternative THE SYSTEM SHALL return both, and leave the choice to the reader that consumes them — the measurement found HTML the better source where the body matters at all.
WHEN an attachment's declared content type disagrees with its filename extension THE SYSTEM SHALL return both, because 155 of 644 PDFs in the measured mailbox arrive as `application/octet-stream` and 4 as `application/.pdf`.

---

## Storage

WHEN the Thunderbird integration stores anything THE SYSTEM SHALL keep it in its own LiteDB database, separate from the main database and from every other integration's store.
WHEN the integration's database context is built THE SYSTEM SHALL expose one `unit -> ILiteCollection<T>` getter per collection and a `Dispose` that closes the underlying database.
WHEN the integration's database context is built THE SYSTEM SHALL warm LiteDB's entity mapping for **every** entity with `BsonMapper.Global.ToDocument` before returning — a scan runs on a background thread, which is exactly the case where a half-built global mapping silently drops a property.
WHEN entities are declared THE SYSTEM SHALL declare them as mutable C# classes in a `.Database.Models` project, because LiteDB needs settable properties.
WHEN the integration stores data THE SYSTEM SHALL store only Thunderbird's own facts — the profile root, the discovered profiles and accounts, their folder lists, the selected account, the cached message counts and the scan watermarks. Nothing about invoices, suppliers or templates.

---

## Architecture

WHEN the integration is built THE SYSTEM SHALL reference `MyDogsbody.Domain` and nothing else outward.
WHEN the domain declares what it needs from Thunderbird THE SYSTEM SHALL declare it as function types, and the domain SHALL NOT name `ILiteCollection`, a profile path, an mbox offset, a MIME type or a `prefs.js` key.
WHEN a store function is written THE SYSTEM SHALL take its dependencies first, its input last, and return `Result<'T, MyDogsbodyException>` written with `handleError`, with one `ActionNames` entry per function.
WHEN the composition root wires this integration THE SYSTEM SHALL do so in a factory with no module-level I/O, so it is testable without reaching `Startup.fs`.
WHEN the UI consumes this integration THE SYSTEM SHALL reach no further than an API record of functions speaking `MyDogsbody.UI.Types`.

---

## The WPF host

WHEN the folder dialog is provided THE SYSTEM SHALL implement it in the WPF host using the native folder dialog, and SHALL register it so the UI receives it as a function.
WHEN the host is changed THE SYSTEM SHALL keep the change to the folder dialog and its registration — this is the first change in the series to touch the host, and it should stay the smallest possible change (friction #12).
WHEN the UI is tested THE SYSTEM SHALL substitute the folder dialog with a lambda, so no test opens a window.

---

## User interface

WHEN a user navigates to `/settings/mail-accounts` THE SYSTEM SHALL display the chosen profile folder, a Browse button, a scan-for-accounts action, and the table of discovered accounts.
WHEN the accounts table is displayed THE SYSTEM SHALL show, per account: display name, email addresses, store format, folder count, on-disk size, and a radio to select it for import.
WHEN a scan for accounts is running THE SYSTEM SHALL show that it is running and SHALL NOT block the interface.
WHEN a scan for accounts finds directories it could not read THE SYSTEM SHALL list them with their reasons.
WHEN an account is configured but its store directory is missing THE SYSTEM SHALL show it in the table marked as such, not omit it.
WHEN an operation fails THE SYSTEM SHALL display the message in a `MudAlert`, and clear it on the next success.
WHEN the page's state is modelled THE SYSTEM SHALL use `cval` / `aval` / `transact` in a module creator taking `startWork` first — no MVU, no dispatch loop, no `Async.Start` inside the module creator.

---

## Testing

### Fixtures

WHEN discovery is tested THE SYSTEM SHALL run against **committed synthetic profile fixtures**, never against a real profile — a test that depends on one machine's mailbox is not a test.
WHEN a fixture is built THE SYSTEM SHALL reproduce the measured profile's shapes: gapped account numbering, a `lastKey` higher than any real account, a stale absolute `directory` alongside a correct `directory-rel`, orphan mail directories with no account, an account whose store directory is missing, an account with two identities, `.sbd` nesting several levels deep, and `.msf` files with no matching mbox.
WHEN maildir is tested THE SYSTEM SHALL run against synthetic fixtures only (Q4.11) — the measured profile is 100% mbox, so there is no real maildir to verify against, and that must be stated in the change description rather than implied.
WHEN the reader is tested THE SYSTEM SHALL include a fixture whose final message is deliberately truncated, and a fixture with a message carrying no `Message-ID`.

### Levels

WHEN a domain function is added THE SYSTEM SHALL have a unit test written **before** the implementation, asserting every field of the success output and the exact error case with its payload.
WHEN the adapters are tested THE SYSTEM SHALL run against real files in a fresh temp directory per test, deleted afterwards.
WHEN the integration's LiteDB store is tested THE SYSTEM SHALL use a fresh temp database per test, `connection=direct`, disposed before the file is deleted, with the delete asserted to have succeeded.
WHEN a dependency function type is published THE SYSTEM SHALL have one shared contract suite run against the real adapter **and** every fake.
WHEN the entity mapper is added THE SYSTEM SHALL have a contract test asserting it field-for-field in both directions, and a persisted-shape test asserting the stored document's field names — LiteDB is schemaless, so a renamed property silently orphans stored data.
WHEN the mail accounts flow is complete THE SYSTEM SHALL have an E2E test covering choosing a folder, scanning for accounts, selecting one, a walk that hits an unreadable directory, and a failure showing an alert.
WHEN the discovery fixture modelled on the measured profile is scanned THE SYSTEM SHALL find exactly the accounts `prefs.js` declares and none of the orphan directories. **That count is the acceptance test for discovery.**
WHEN a test is added THE SYSTEM SHALL tag it with its level.

### Gate

WHEN this change is complete THE SYSTEM SHALL build the whole solution with zero errors and pass the whole suite with zero failures and zero skips.
WHEN this change is complete THE SYSTEM SHALL have been verified by hand against the real profile with Thunderbird running, and the result recorded in the change description — including the account count found and the time a full folder enumeration took.

---

## Edge cases

WHEN the chosen folder contains no `prefs.js` at all THE SYSTEM SHALL report "no Thunderbird profile found here", not an empty account list.
WHEN a `prefs.js` is present but malformed THE SYSTEM SHALL report that profile as unreadable and continue with any others.
WHEN a `prefs.js` entry contains an escaped character or an embedded quote THE SYSTEM SHALL decode it correctly.
WHEN an account has no identities THE SYSTEM SHALL list it with no email address rather than dropping it.
WHEN two profiles declare accounts with the same email address THE SYSTEM SHALL list both, qualified by profile path.
WHEN a folder file is zero bytes THE SYSTEM SHALL treat it as an empty folder, not an error.
WHEN a folder's name differs in case between the mbox file and its `.sbd` directory THE SYSTEM SHALL still pair them, because Windows paths are case-insensitive.
WHEN a message spans a line that begins with `From ` inside its body THE SYSTEM SHALL not treat it as the start of the next message, per mbox quoting.
WHEN an attachment has no filename THE SYSTEM SHALL return it with a generated name that states its content type, so a later unsupported-format report can name what arrived.
WHEN a message is larger than a reasonable bound THE SYSTEM SHALL read it without loading the whole folder into memory.
WHEN the profile folder is on a network path or a removable drive that has gone away THE SYSTEM SHALL report that specifically rather than hanging.

---

## Out of scope

- **Anything invoice-shaped.** No extraction, no templates, no suppliers, no ledger.
- **The scan window picker.** It belongs to the invoices page and to the main database (change #4). This change is handed a cutoff; it does not decide one.
- **Reading `global-messages-db.sqlite` (gloda).** It is a SQLite index and would be faster, but it is not guaranteed to be enabled or current. Recorded as a possible optimisation, not built (friction #4).
- **Writing anything to the profile.** Read-only by construction, and tested that way.
- **Folder-level selection.** One account at a time, per Q4.1.
- **Watching the profile for changes.** A scan is something the user asks for.
