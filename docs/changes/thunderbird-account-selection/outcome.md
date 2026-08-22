# Outcome — Thunderbird account selection

Change **#3 of 7**. See [`requirements.md`](requirements.md), [`design.md`](design.md),
[`tasks.md`](tasks.md).

## Gate

- `dotnet build MyDogsbody.sln` — **0 errors**, 2 pre-existing warnings (both in
  `MyDogsbody.Tests`, neither touched by this change: `FS0760` in `PdfDocumentReaderTests.fs`,
  `FS0020` in `CredentialDependencyContractTests.fs`).
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` — **1005 tests, 0 failures, 0 skips**, all
  four levels present (re-measured during PR #17's round-1 review-fix; the count recorded here at
  the time this change was first closed, 878, undercounted what the committed test project actually
  contains. PR #17's round-2 review-fix then added 21 tests with its five fixes — see per-level
  figures below, reproduced with `--filter "Level=..."`):

  | Level | Round 1 | Round 2 |
  | --- | --- | --- |
  | Unit | 532 | 544 |
  | Integration | 198 | 205 |
  | Contract | 232 | 233 |
  | E2E | 22 | 23 |
  | **Total** | **984** | **1005** |

  No trustworthy pre-branch baseline was captured before this change started (the figure
  CLAUDE-project.md carried, 399, already predated the `invoice-ledger-foundation` and
  `invoice-templates` changes this one branches from, so a before/after delta against it would be
  meaningless). CLAUDE-project.md's *Build state* section is corrected to the measured total above.
- `Contracts/DomainIsolationTests.fs` (3 tests) and the `AssertDomainReferencesNothing` build
  target both still pass — `MyDogsbody.Domain` gained `MailAccounts/` and still has zero
  `ProjectReference` elements.
- `MyDogsbody/MainWindow.xaml.cs` is the **only** file changed in the WPF host project (`git diff
  --stat` — one file, +23/-1), adding exactly the `FolderPicker` construction and its
  `AddSingleton` registration. See *Design deviations* for why this one file had to change at
  all, and *A known intermittent failure* below for the one flake this change's added parallel
  LiteDB-context tests made more visible.

## A known intermittent failure (not new)

Several full-suite runs during this change hit `System.InvalidOperationException: Collection was
modified; enumeration operation may not execute` from `LiteDB.BsonMapper.SerializeObject`, inside
`ThunderbirdDatabaseContextModule.getDatabaseContext`'s warm-up line. This is the pre-existing,
documented LiteDB global `BsonMapper` first-use race (CLAUDE-project.md → *Per-integration
databases*) — the warm-up narrows it but does not close it, and closing it needs a process-wide
lock and its own change folder, which is explicitly out of scope here. This change adds five more
LiteDB entities and several new parallel test classes that construct a `ThunderbirdDatabaseContext`,
which is why the race surfaced more often during this change's development than it apparently has
before — not because anything here is wrong, but because there are now more chances to hit an
existing gap. Re-running the full suite reliably clears it; the totals recorded above are from a
clean run.

## Manual verification (11.4)

Run 2026-08-22 against the real profile,
`C:\Users\jygcn\AppData\Roaming\Thunderbird\Profiles\49stkd1y.default`, with Thunderbird running
throughout (6 processes — its normal multi-process shape on this machine). Driven through a
throwaway console harness referencing `MyDogsbody.Startup` (per CLAUDE-project.md's guidance for
exercising the real composition root without the WPF host), kept outside the repository and
deleted afterward along with the `Thunderbird.db`/`MyDogsbody.db` files it produced — those held a
real snapshot of account discovery data (email addresses), which is why the numbers below are
reported in aggregate only. This matches background.md's own policy for this series: no invoice
contents, amounts, references or addresses in version control.

- **Accounts found: 10** — matching the number `prefs.js` declares (`mail.accountmanager.accounts`
  lists exactly 10 keys), not the larger count a directory listing under `ImapMail/` would produce.
  This is the acceptance test for discovery (requirements.md), and it passed against the real
  profile, not only the synthetic fixture.
- **Full folder enumeration (the whole profile tree, all 10 accounts): 0.62 seconds.** Confirms
  friction #14's expectation that enumeration is cheap because it only stats file sizes — it never
  reads message content. This is the number Q1.9 (immediate rescan) was accepted conditionally on;
  at well under a second, an explicit Refresh button is not warranted and immediate rescan stays
  settled, at least for enumeration. (`ScanForAccounts` also updates `Unreadable`; none were
  found — 0 unreadable directories in this run.)
- **Headers-only message count for the largest scannable account (3.36 GB across 59 folders):
  1.70 seconds, 570 messages.** This is the number friction #14 and Q1.9 actually ride on for
  change #4's rescan-on-every-click question, since counting is what reads every folder's bytes
  (enumeration does not). Under two seconds for the single largest account is comfortably cheap
  enough that change #4 should not need to fall back to an explicit Refresh button purely on
  timing grounds — though change #4's own workload (applying a cutoff and parsing kept messages
  across *all* selected-window accounts, not just counting one) is a different cost shape and
  should still take its own measurement before treating this as settled for the sync path too.
- **The profile was unmodified and Thunderbird noticed nothing.** All reads used
  `FileShare.ReadWrite`; nothing in this change's code path ever opens a file for write. Thunderbird's
  process list (6 processes, same PIDs) was identical before and after the run, with no crash,
  restart, or visible disruption.

## Design deviations

- **`MailAccountError` gained an eighth case, `MailAccountIdInvalid of reason: string`, not in
  `design.md`'s listing.** `SelectMailAccountWorkflow` takes a raw string id; the documented DU had
  no case for a malformed (empty) one, only for one that parses but names no known account. Same
  shape as `invoice-ledger-foundation`'s `PaymentTermInvalid` addition. PR #17's round-2 review
  added a ninth, `ProfileRootUnreachable of path * reason` — see finding 5 below for why the
  documented cases could not express it.
- **The "eleven" dependency function types design.md's *Contract* section counts** turn out to be
  the ten domain types `MailAccountsTypes.fs` actually declares plus `FolderPicker`
  (`MyDogsbody.UI.Types`, Phase 7) — a UI-level function type, not a domain one, but one this
  change also publishes and gives its own (fake-only; it opens a native dialog, so there is no
  headless "real" to run against) contract suite.
- **`ThunderbirdFolderScanner`/`ThunderbirdAccountReader`/`MailFolderEnumerator`/`MailFolderReader`
  construct `MailAccountError` directly and never call `handleError`**, unlike the established
  outer-ring convention (`Result<_, MyDogsbodyException>` via `handleError`, translated once in the
  `*ApiFactory`). Only `ThunderbirdStore.fs` (genuine LiteDB CRUD) follows that convention. This
  follows design.md's own *Error-handling approach* table, which marks a locked file or malformed
  `prefs.js` as *expected*, constructed with context at the point of failure — the alternative
  (raise, catch generically in `handleError`, reconstruct the specific path/reason from a bare
  exception message in the factory) would have thrown away information design.md's own error cases
  need. `ThunderbirdStore.updateCachedMessageCount` was added beyond design.md's original file list,
  needed for "cache it with the time it was taken" without routing through `saveMailAccounts`
  (which replaces the whole account set and would needlessly re-touch every folder row).
- **The per-row account selection control is a `MudCheckBox`, not a `MudRadio`/`MudRadioGroup`.**
  Stated plainly in `tasks.md` (Phase 8.3): a `MudRadioGroup` wrapping radios spread across
  `MudTable`'s independently-rendered `RowTemplate` invocations was judged an unverified risk to
  get right without being able to render the page by hand during this pass. The checkbox drives
  the exact same `SelectAccount` action and reads as a single-choice control in practice, but it
  is not a true HTML radio group — swapping it for a real `MudRadioGroup` is a reasonable
  follow-up if the visual distinction matters.
- **`MailAccountUiType`/`MailFolderUiType`/`DiscoveryResultUiType`/`MailAccountApi`** (tasks.md
  lists these under Phase 8.1) were actually declared during Phase 6, because the composition root
  genuinely needed them to exist before it could be written. Phase 8.1 ended up needing only
  `Modules/MailAccountsBrowserModule.fs`.

## Two real bugs found and fixed along the way

1. **The junction-loop guard in `ThunderbirdFolderScanner` didn't actually work.** It canonicalised
   a visited directory with `Path.GetFullPath`, which only normalises `.`/`..`/separators — it does
   **not** resolve a directory junction or symlink to what it actually points at. A junction
   pointing at an ancestor directory therefore looked like "a different path" every time it was
   walked, and the depth bound was the only thing stopping an infinite loop (a test proved this:
   before the fix, scanning a fixture with such a junction found 6 profile directories instead of
   1). Fixed with `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)`, which resolves a
   reparse point to its real target before the visited-set comparison.
2. **`MailFolder.RelativePath` is a *logical* hierarchy path, and naive `'/'`-to-separator
   substitution silently produced the wrong filesystem path for anything nested under mbox's
   `.sbd` convention.** mbox nests a folder's children under a `.sbd`-suffixed *sibling* directory
   (`Music.sbd/Surrey Hills Orchestra`), not a same-named one (`Music/Surrey Hills Orchestra`) — so
   `MailFolderReader.readFolder` and `countMessages` were opening nonexistent files for every
   nested folder and silently treating them as empty (a test proved this: `countMessages` against
   the four-folder `imap.alpha.example.com` fixture returned 2, not 4). Fixed by adding
   `MailFolderEnumerator.resolvePath storeDirectory format relativePath`, the format-aware inverse
   of enumeration, reused by both call sites.

## Five defects PR #17's round-2 review found and fixed

Round 2 re-read the code cold. Five findings, all raised by the review itself (the PR carried no
open review comments at any point).

1. **A message whose header block ended in a CRLF blank line was silently dropped.**
   `MailFolderReader.headerBlockAndRest` looked only for `"\n\n"`. RFC 5322 mandates CRLF, and a
   message written to the store exactly as it arrived over IMAP or SMTP keeps it — so
   `headerBlockAndRest` returned `None`, `processSegment` dropped the message with no error and
   nothing on screen, and `readMboxFile` classified a final CRLF message as torn. Measured: a
   two-message CRLF folder returned `[]`, and `countMessages` on it returned 1 of 2. Nothing
   caught it because `.gitattributes` pins every committed fixture to LF — that file's own comment
   records that CRLF "silently breaks every test that reads one", which was treated as a checkout
   problem rather than the parser defect it also is. Both separators are now recognised, earliest
   wins.
2. **The recorded watermark offset drifted backwards by one byte per message, and the deficit
   accumulated.** `splitIntoMessages` rebuilt each segment with `String.Join("\n", ...)`, which
   drops the newline separating it from the next segment, and `readMboxFile` summed those lengths;
   bytes lying before the first boundary in the buffer were not counted at all. Measured: a
   three-message folder of 771 bytes recorded `OffsetReached = 769`. Since each incremental read
   adds a further (messages − 1), the offset walks backwards until it reaches back past an
   already-read message's `From ` line and re-emits it. Segments are now byte slices carrying
   their offset within the buffer, so the offset reached is the offset actually reached. Every
   existing incremental test passed over this because each used a single-message file, the one
   case where `String.Join("\n", s.Split('\n')) = s` exactly.
3. **The accounts table did not show on-disk size.** requirements.md asks for it twice (*Selecting
   an account*, *User interface*) and gives the reason in *Listing folders* — the page states what
   a scan will cost before it is run. The data was already collected and already reached the UI on
   `MailFolderUiType.SizeBytes`; only the column was missing. This was previously recorded here as
   optional item **O.2**, described as "per-folder sizes", which mis-scoped a required per-account
   figure as a nicety. The row now shows the account's total with the scannable subset beneath it,
   because the Q4.8 exclusions are most of the bytes on a real profile.
4. **A folder larger than one array could not be read, and failed in two different wrong ways.**
   `readMboxFile` computed `int (totalLength - fromOffset)`, which wraps silently past 2 GiB — the
   measured profile has a single 2.5 GB mbox — after which `Array.zeroCreate` threw a negative-length
   `ArgumentException` that neither `with` handler catches, escaping the whole API call; in
   `countMessages` the same allocation was swallowed by `with _ -> 0` and reported as an empty
   folder. A guard now reports the folder as `MailFolderUnreadable` with the size in the message,
   so the other folders still return. **This one carries no test**: reproducing it needs a >2 GB
   fixture, and none is committed. Reading such a folder *without* buffering it whole is
   requirements.md's "read it without loading the whole folder into memory" and still is not
   implemented — see **O.5**.
5. **A profile folder that has gone away was reported as "no profile found there".**
   `ThunderbirdFolderScanner.scan` cannot canonicalise a path that is not there
   (`DirectoryInfo.ResolveLinkTarget` throws `DirectoryNotFoundException`, `canonicalize` returns
   `None`), so the walk returned an empty outcome indistinguishable from "walked it, found no
   `prefs.js`", and `NoProfileFound` was what the user saw. requirements.md asks for that state to
   be reported specifically, twice (*Choosing the profile folder*, and the network-path /
   removable-drive edge case). `MailAccountError` gains a ninth case,
   `ProfileRootUnreachable of path * reason`, checked in `MailAccountApiFactory` beside the
   existing `NoProfileFound` decision. The stored path is kept either way, as required.

## Not implemented (Optional, deferred)

- **O.1** Reading `global-messages-db.sqlite` (gloda) as a fast path — not guaranteed enabled or
  current, and the measured numbers above show the slow path is already fast enough.
- **O.2** *(closed by round 2, finding 3 — the accounts table now shows on-disk size.)* Per-folder
  sizes broken out folder by folder, rather than the per-account total and scannable subtotal the
  table now shows, remain unbuilt.
- **O.3** Letting the user override the folder exclusions (Trash/Deleted/Junk/Sent/Drafts).
- **O.4** Detecting a profile being written by a different Thunderbird version.
- **O.5** **Streaming the mbox reader.** requirements.md → *Edge cases* asks that a large message be
  read "without loading the whole folder into memory", and `readMboxFile` still buffers the whole
  span. Round 2 added a guard so an over-large folder is reported rather than crashing or counting
  zero (finding 4), but the requirement itself needs a streaming line reader that keeps exact byte
  offsets. That is a design change, not a defect fix, and wants its own change folder.
- **O.6** **Maildir folders record no watermark.** `MailFolderReader.readFolder` consults and
  updates a watermark on the mbox branch only. requirements.md → *Incremental scanning* is written
  unconditionally, but its three fields — file size, modification time and *offset reached* — are
  mbox concepts; a maildir folder is a directory of one-message files with no offset, so what a
  maildir watermark should even be is a design question rather than a bug. Deliberately left for
  the change that decides it.
- **O.7** **`MailFolderReader.read` discards each folder's failure reason.** It skips an unreadable
  folder (`| Error _ -> []`) so the others still return, which is what requirements.md asks for,
  but the reason never reaches anyone — the requirement also asks for "a reason a user can act
  on". Surfacing it means widening the `ReadMailFolder` dependency type, which change #4 consumes;
  it belongs to that change rather than to this one.
- **O.8** `MailAccountApiFactory` binds `loadWatermark` and `saveWatermark` but nothing uses them
  yet — `MailFolderReader.read` is not wired into `MailAccountApi`, because reading mail is change
  #4's surface. Harmless today, and the bindings are what change #4 will attach `ReadMailFolder`
  to; noted so it is not mistaken for live wiring.

## Stated plainly: maildir ships verified against synthetic fixtures only

Per Q4.11 — the measured profile is 100% mbox (`berkeleystore`), so there is no real maildir data
in this environment to verify the maildir reader against. `Fixtures/ThunderbirdProfiles/maildir-shape/`
is entirely synthetic, and `MailFolderEnumerator`'s and `MailFolderReader`'s maildir code paths are
tested only against it. This is a real, acknowledged coverage gap for maildir specifically, not an
oversight — restated here per CLAUDE.md's "no silently dropped level" rule, even though the level
itself (Integration) is present and green.
