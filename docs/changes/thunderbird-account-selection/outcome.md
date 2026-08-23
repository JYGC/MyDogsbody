# Outcome — Thunderbird account selection

Change **#3 of 7**. See [`requirements.md`](requirements.md), [`design.md`](design.md),
[`tasks.md`](tasks.md).

## Gate

- `dotnet build MyDogsbody.sln` — **0 errors**, 2 pre-existing warnings (both in
  `MyDogsbody.Tests`, neither touched by this change: `FS0760` in `PdfDocumentReaderTests.fs`,
  `FS0020` in `CredentialDependencyContractTests.fs`).
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` — **1061 tests, 0 failures, 0 skips**, all
  four levels present (re-measured during PR #17's round-1 review-fix; the count recorded here at
  the time this change was first closed, 878, undercounted what the committed test project actually
  contains. PR #17's round-2 review-fix then added 21 tests with its five fixes, round 3 a further
  16, round 4 two more, round 5 twelve, round 6 three, round 7 two, round 8 ten, round 9 four and
  round 10 seven — see per-level figures below, reproduced with `--filter "Level=..."`):

  | Level | Round 1 | Round 2 | Round 3 | Round 4 | Round 5 | Round 6 | Round 7 | Round 8 | Round 9 | Round 10 |
  | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
  | Unit | 532 | 544 | 554 | 556 | 559 | 559 | 560 | 560 | 561 | 564 |
  | Integration | 198 | 205 | 209 | 209 | 216 | 219 | 219 | 222 | 222 | 223 |
  | Contract | 232 | 233 | 235 | 235 | 236 | 236 | 236 | 242 | 244 | 246 |
  | E2E | 22 | 23 | 23 | 23 | 24 | 24 | 25 | 26 | 27 | 28 |
  | **Total** | **984** | **1005** | **1021** | **1023** | **1035** | **1038** | **1040** | **1050** | **1054** | **1061** |

  Round 4 adds two Unit tests and changes four existing ones rather than adding to them — its
  finding was that two integration tests were aimed at the wrong boundary, so retargeting them was
  the fix's own red-first step. See *Two defects PR #17's round-4 review found and fixed*.

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
  **Round 3 caveat: treat the 570 as a lower bound, not a measurement.** Round 3's finding 2 shows
  `countMessages` silently contributed 0 for any folder over ~2 GiB, and this profile has a 2.5 GB
  mbox — so if that folder was in the scannable set, this run counted it as empty and said nothing.
  The timing figure stands (the folder was opened either way); the message total needs re-measuring
  once O.5 makes such a folder readable.
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
   so the other folders still return. Reading such a folder *without* buffering it whole is
   requirements.md's "read it without loading the whole folder into memory" and still is not
   implemented — see **O.5**. **Round 3 correction:** as shipped in `3e71b20` this fix reached
   `readMboxFile` only. `countMessages` has its own inline allocation and never consulted the
   guard, so the sentence above overstated what landed — see round 3's finding 2, which closes it
   for real. Round 2 also recorded this fix as carrying no test, on the grounds that a >2 GB
   fixture cannot be committed; that is true of a *committed* fixture but not of one a test makes
   and deletes, and round 3 supplies both the missing coverage and the missing fix.
5. **A profile folder that has gone away was reported as "no profile found there".**
   `ThunderbirdFolderScanner.scan` cannot canonicalise a path that is not there
   (`DirectoryInfo.ResolveLinkTarget` throws `DirectoryNotFoundException`, `canonicalize` returns
   `None`), so the walk returned an empty outcome indistinguishable from "walked it, found no
   `prefs.js`", and `NoProfileFound` was what the user saw. requirements.md asks for that state to
   be reported specifically, twice (*Choosing the profile folder*, and the network-path /
   removable-drive edge case). `MailAccountError` gains a ninth case,
   `ProfileRootUnreachable of path * reason`, checked in `MailAccountApiFactory` beside the
   existing `NoProfileFound` decision. The stored path is kept either way, as required.

## Four defects PR #17's round-3 review found and fixed

Round 3 re-read the code cold again, including round 2's own fixes. Four findings, all raised by
the review itself; the PR has still never carried an open review comment.

1. **A folder watermark could not survive the store, so every incremental read was a silent full
   re-read.** `ScanWatermarkEntity.ModifiedAt` was a `DateTime` column, and LiteDB does two things
   to one: it truncates to whole milliseconds, and it returns the value as `DateTimeKind.Local`
   with the ticks shifted by the machine's UTC offset. `readFolder` stores
   `File.GetLastWriteTimeUtc` (Kind `Utc`, 100-ns NTFS ticks) and then compares the loaded value to
   the file's own mtime with `=`, so the comparison could never hold. Measured on this machine
   (UTC+10): stored `639228150000001234` ticks, loaded `639228510000000000` — out by ten hours less
   the 1234 sub-millisecond ticks. Both watermark branches therefore fell through to the full
   re-read, and a second read of an unchanged two-message folder returned both messages again
   instead of none. Fixed by persisting UTC ticks as an `int64` (`ModifiedAt` → `ModifiedAtTicksUtc`
   — a rename, asserted as one in the persisted-shape suite); nothing between the mapper and disk
   can reinterpret an `int64`. The existing watermark tests all passed over this because they used
   `DateTime(2026, 8, 20)` — Kind `Unspecified`, midnight, whole seconds — which is the one subset
   that survives a lossy store unchanged, so the in-memory fake and the real adapter agreed over a
   difference only production ever saw. The shared contract suite now runs a real filesystem mtime
   through both, which is exactly the drift that suite exists to catch.
2. **`countMessages` reported a folder it could not read as holding no messages.** Round 2's
   over-2-GiB guard went into `readMboxFile`; `countMessages` has its own inline
   `Array.zeroCreate (int stream.Length)` and never got one, so `int` still wrapped negative, the
   allocation still threw, and `with _ -> 0` still turned that into "this folder is empty". The
   user asked how many messages an account holds and got a confident total that silently omitted
   their largest folder — on the measured profile, one 2.5 GB mbox. Both callers now share one
   pure `MailFolderReader.bufferSpan`, and `countMessages` returns `MailFolderUnreadable` naming
   the folder and its size. **Stated plainly as a deliberate regression:** until O.5 lands,
   *Count messages* now fails on an account holding a folder over ~2 GiB rather than
   under-reporting it. A wrong number nobody can see is worse than an error that says which folder
   and how big.
3. **A stored offset past the end of the file crashed out of the whole API.** `readFolder` measures
   the size *before* opening the file, so a folder that grows in between records an `OffsetReached`
   past the `SizeBytes` beside it; a later compaction to a size between the two leaves that offset
   past the end. `readMboxFile` then asked for a negative buffer, and
   `System.ArgumentException: The input must be non-negative. count = -500` escaped both `with`
   handlers, `readFolder`, `read` and the API call — the same defect as finding 2, from the other
   end of the range. `bufferSpan` restarts the span at 0 for an offset outside the file, so the
   folder is simply read in full: re-reading a message is recoverable, crashing is not.
4. **The chosen profile folder was displayed from the string handed in, not from what was stored.**
   `MailAccountsBrowserModuleCreators.setProfileRoot` assigned `profileRootCval` optimistically
   instead of reloading, against CLAUDE-project.md → *UI*: "A write reloads." `ProfileRootPath.create`
   trims, so a padded path was stored trimmed and displayed padded until the next launch. Lowest
   severity of the four and not reachable through the app today — the WPF `FolderPicker` never
   produces a padded path — but it is the stated convention, and change #4 may set the root from
   somewhere less well-behaved.

The `bufferSpan` guards behind findings 2 and 3 are exercised by an integration test that creates a
file one byte past the limit with `FileStream.SetLength` and deletes it: NTFS records the length
without zeroing the clusters, so it costs about 3 ms and no measurable I/O. That is what makes
round 2's "no test — it needs a fixture over 2 GB" unnecessary. The test is Windows/NTFS-specific,
which this repository already is (the host is WPF). *(Round 4 correction: that file was one byte
past `Array.MaxLength`, which is nearly twice the reader's real limit — see round 4's finding 1.)*

## Two defects PR #17's round-4 review found and fixed

Round 4 re-read the code cold, with particular suspicion of `bufferSpan` and its callers, since
rounds 2 and 3 had both rewritten them. Two findings, both raised by the review itself; the PR has
still never carried an open review comment.

1. **The size guard was set to nearly twice the reader's real limit, so both defects rounds 2 and 3
   reported as closed were still reachable — between 1.0 GiB and 2.0 GiB.** `bufferSpan` refused a
   span over `Array.MaxLength` (2,147,483,591 bytes). But the buffer is only ever an intermediate:
   `splitIntoMessages` turns the whole of it into a Latin1 string on its first line, and .NET caps
   a string at **1,073,741,791** chars — the 2 GB object-size ceiling at two bytes per char, a
   little under *half* what an array holds. Measured on this machine with 51 GB of memory free, so
   it is a hard runtime ceiling and not memory pressure: `new string('a', 1_073_741_792)` is
   rejected on the size alone.

   A folder in that window therefore sailed through the guard and died converting. Measured
   against `f964b29` on sparse files of 1.12 GiB and 1.49 GiB:

   | | Before | After |
   | --- | --- | --- |
   | `countMessages` | `Ok 0` — the folder silently counted as empty | `Error (MailFolderUnreadable (path, "The folder has 1200000000 bytes still to read, more than this reader can buffer in one pass (1073741791 bytes)."))` |
   | `readFolder` | `System.OutOfMemoryException` at `Latin1Encoding.GetString`, out of `splitIntoMessages` → `readMboxFile` → `readFolder` → `read` → the whole API call, past both `with` handlers | the same `Error (MailFolderUnreadable …)` |

   Those are round 3's finding 2 and finding 3 verbatim, still live at every size in the window.
   The two existing integration tests missed it because they created a file one byte past
   `Array.MaxLength`, i.e. past *both* ceilings — the only size at which the mis-set guard still
   looks right. Fixed by sizing the guard on `MailFolderReader.MaxBufferableBytes` (the string
   ceiling) rather than on `Array.MaxLength`, and by retargeting those two tests to one byte past
   *it*, where they were red for exactly the two behaviours above. A unit test pins the constant to
   the runtime ceiling by asserting one char more cannot be allocated — free, because the runtime
   rejects it on size without allocating anything — so a future runtime that lowers the ceiling
   fails there rather than as an `OutOfMemoryException` in production. The error message now names
   the span still to read rather than "the folder is N bytes", which was the wrong number on an
   incremental read of a partly-consumed folder.

   Side effect worth having: the guard now short-circuits before allocating, so
   `MailFolderReaderTests` runs in 258 ms rather than 1 s, and the two oversized fixtures cost
   1 GiB of disk each instead of 2 GiB.
2. **Stale figures the previous rounds left behind.** `tasks.md` **11.2** still recorded the
   round-2 totals (1005) after round 3 added 16 tests and corrected the same figure in
   `outcome.md` and `CLAUDE-project.md` — the third of the three places round 1 had gone through.
   And `CLAUDE-project.md` lists, twice, the database files this application opens in its working
   directory; this change adds a fourth, `Thunderbird.db`, and neither list was updated, so a
   developer following *Commands → Run*'s "delete them after a manual test" leaves it behind for
   `git status`. Both corrected. No behavioural change, so neither has a red-first test — stated
   here rather than counted as covered.

## Three defects PR #17's round-5 review found and fixed

Round 5 re-read the diff cold, deliberately spending its attention away from `MailFolderReader`'s
size guards — which rounds 2, 3 and 4 had all rewritten — and on the parts that had had less: the
account reader, the store, the ApiFactory, the UI, and this change's own specs. Three findings, all
raised by the review itself; the PR has still never carried an open review comment.

1. **One message MimeKit could not parse threw an uncaught `FormatException` out of the whole
   account read — and ordinary mail reaches it.** `parseMessage` calls `MimeMessage.Load`, which
   raises `FormatException("Failed to parse message headers.")` for content that is not a message.
   That is neither an `IOException` nor an `UnauthorizedAccessException`, so it walked straight past
   `readMboxFile`'s and `readMaildirFolder`'s `with` clauses, out of `readFolder`, out of `read`,
   and out of the call — from functions whose whole signature says `Result<_, MailAccountError>`,
   and in direct contradiction of design.md's own contract for `read` ("a locked file reports
   `MailFolderUnreadable` and the other folders still return").

   This is not a corrupt-file case. mbox carries no length header, so `splitIntoMessages` has to
   guess a boundary from an unquoted `From ` at the start of a line preceded by a blank one — which
   a plain-text body signing off `From the accounts team,` satisfies exactly. The half after that
   false boundary begins with body text where RFC822 headers should be. Measured against `f37cc56`
   on the new `Fixtures/Mbox/UnquotedFromInBody.mbox` (two entirely ordinary messages, one such
   signature line):

   | | Before | After |
   | --- | --- | --- |
   | `readFolder` (mbox) | `System.FormatException: Failed to parse message headers.` at `MimeKit.MimeParser.ParseMessage`, out of `parseMessage` → `processSegment` → `readMboxFile` → `readFolder` | `Ok` with both real messages — `<unquoted-1@example.com>` and `<unquoted-2@example.com>` — every field asserted, offset advanced to EOF (498) |
   | `read` (whole account, one such folder plus a good one) | the same exception, out of `read` too, so **every** folder of the account was lost | `Ok` with 3 messages |
   | `readFolder` (maildir) | the same exception | `Ok`, the parseable message returned |
   | `countMessages` | unaffected (it never parses a body) | unchanged |

   Fixed with `MailFolderReader.tryParseMessage`, used by both the mbox and the maildir branch: a
   segment MimeKit cannot parse is **discarded and the folder keeps going**, the same treatment a
   torn final message gets (requirements.md: "discard that partial message and return everything
   before it, rather than failing the folder"), except that the offset still advances past it —
   unlike a torn message this will never become parseable, so stopping before it would re-read the
   same bytes on every later scan. Discarding was chosen over failing the folder deliberately: the
   fragment is the tail of a message that *is* returned, so dropping it costs a body's last lines,
   whereas failing the folder would cost every message in it — on a real INBOX, all of them, on
   every scan.
2. **requirements.md and design.md both require a cleared selection to be reported; nothing
   reported it, and nothing recorded that it was unbuilt.** requirements.md → *Selecting an account*:
   "WHEN the selected account no longer appears in a fresh discovery THE SYSTEM SHALL clear the
   selection **and say so**, rather than leaving a selection pointing at nothing." design.md's *Unit*
   section says the same ("...is cleared, **and the workflow says so**"). The workflow cleared it
   and returned `unit`, so the fact died at `reconcileSelection`: `DiscoveryResult` had no field for
   it, the UI had nothing to render, and the only thing the user saw was their ticked row silently
   un-ticking. Unlike **O.9** below, this was not recorded as deferred anywhere — it had simply been
   dropped, and design.md contradicted itself about it (its prose asks for it; its own
   `DiscoveryResult` listing has no field for it, exactly as O.9 describes for three other fields).

   Fixed by carrying one `bool` the length of the pipeline: `reconcileSelection` now returns whether
   it cleared anything, `DiscoveryResult` and `DiscoveryResultUiType` gained `SelectionCleared`, the
   top mapper carries it, `MailAccountsBrowserModule` gained `SelectionClearedAval`, and the page
   shows a `Severity.Warning` `MudAlert` — its own aval rather than a message pushed into
   `ErrorAval`, because nothing failed and the alert channel is reserved for failures. Set from
   *every* successful scan, not only a clearing one, so a later scan that clears nothing takes the
   notice back down — the same "cleared by the next success" rule `ErrorAval` follows. The
   `DiscoverMailAccounts` adapter always reports `false` (it never sees the stored selection) and
   the workflow overwrites it; the contract suite asserts that of both implementations.
3. **`ThunderbirdStore.updateCachedMessageCount` had no test at any level.** Every other function in
   the module has five to seven; this one had none — no Ok path, no not-found path, no `ActionNames`
   assertion — while being what persists the figure the accounts table's *Message count* column
   shows. `MailAccountApiFactoryTests` reached it indirectly but asserted only the count, discarding
   the timestamp as `_takenAt`. No behavioural change, so **no red-first test**: the four added tests
   were green on first run, and are recorded here as closing a coverage gap rather than as covering a
   fix. They assert the count and the instant it was taken (LiteDB hands a `DateTime` back as
   `DateTimeKind.Local`, so the *instant* is what survives — harmless here, and now asserted as such,
   because this column is only ever displayed and never compared for equality, unlike the watermark
   that round 2 had to move onto `ModifiedAtTicksUtc`), every other field of the row surviving the
   in-place update, the neighbouring account staying untouched, a second update replacing rather than
   duplicating, an unknown id changing nothing, and the error path's exact `ActionName`, message and
   preserved inner exception.

## One defect PR #17's round-6 review found and fixed

Round 6 read round 5's own work cold — nothing had reviewed it — concentrating on whether
`tryParseMessage`'s discard-and-advance is right in every case and whether the new `SelectionCleared`
signal is correct end to end. The `SelectionCleared` chain held up (see *the one thing left open*
below); `tryParseMessage` did not, because it closed one exception type rather than the class.

1. **A message whose attachment part has headers but no content yet threw an uncaught
   `NullReferenceException` out of the whole account read — the same escape round 5 fixed, through
   the door it left open.** Round 5's `tryParseMessage` catches `FormatException`, which is what
   MimeKit raises for content it cannot parse as a message *at all*. It is not the only way the
   parse throws. MimeKit leaves `MimePart.Content` **null** for a part whose headers were parsed but
   whose body was not there, and `toMailAttachment`'s first line is `part.Content.DecodeTo content`.
   A `NullReferenceException` is neither a `FormatException`, an `IOException` nor an
   `UnauthorizedAccessException`, so it walked past `tryParseMessage`, past `readMboxFile`'s and
   `readMaildirFolder`'s `with` clauses, out of `readFolder`, out of `read` and out of the whole API
   call — from signatures that say `Result<_, MailAccountError>`, exactly the contract round 5 was
   restoring.

   This is the *ordinary* state of an mbox while Thunderbird is flushing a message with an
   attachment: a part's headers go down before its base64 does. It is not caught by the torn-message
   rule, because that rule asks whether the segment has a header/body separator — and the message's
   own headers and blank line are already on disk by the time the attachment part is being written,
   so `isTorn` says the segment is complete and hands it to the full parse. requirements.md →
   *Reading safely while Thunderbird is running* is precisely the scenario.

   Measured against `7dc0b53`, truncating one realistic invoice email (multipart/mixed, a text part
   and a base64 PDF, 666 bytes) at **every one of its byte offsets** and running the production
   parse path:

   | | Before | After |
   | --- | --- | --- |
   | Offsets throwing `NullReferenceException` (escapes the reader) | **64** | **0** |
   | Offsets throwing `FormatException` (held by `tryParseMessage`) | 52 | 52 |
   | `readFolder` (mbox, new `Fixtures/Mbox/AttachmentContentNotYetWritten.mbox`) | `NullReferenceException` at `MailFolderReader.toMailAttachment` → `parseMessage` → `tryParseMessage` → `processSegment` → `readMboxFile` → `readFolder` | `Ok` with both messages, every field asserted |
   | `read` (whole account, one such folder plus a good one) | the same exception, so **every** folder of the account was lost | `Ok` with 3 messages |
   | `readFolder` (maildir) | the same exception | `Ok`, the message returned |

   Fixed by filtering the part rather than widening the catch: `parseMessage` now maps only a
   `MimePart` that actually has content. Widening `tryParseMessage` to a bare `with _ ->` was
   considered and rejected — this file's own `countMessages` comment records that a blanket handler
   is exactly what turned an over-limit folder into "this folder holds no messages" and hid it for
   two review rounds, so the fix addresses the cause instead of catching its symptom.

   The content-less part is **dropped and the message kept**, rather than the message being discarded
   whole: the bytes are genuinely not in the file, so there is no attachment to report, while the
   message around it — sender, subject, date, body — is entirely readable and is what the reader
   exists to return. Reporting it as a zero-byte `invoice.pdf` instead would turn "not written yet"
   into "this PDF is corrupt" for the invoice pipeline downstream.

   No unit-level seam exists for this: `parseMessage` and `toMailAttachment` are private and reached
   only through a file on disk, so the red-first tests are Integration, the same level and the same
   file as round 5's tests for the identical defect class. Three added; all three were red against
   `7dc0b53` with the `NullReferenceException` above, and the existing
   `AttachmentDeclaredOctetStream` / `AttachmentNoFilename` tests hold the filter honest by proving
   a part that *does* have content is still returned.

### The one thing left open, and why it was not fixed here

`SelectionClearedAval` is set only by a scan, so the warning it drives stays on screen after the
user answers it by picking an account — it comes down on the next scan that clears nothing, not on
the selection itself. Round 5's E2E test covers the on and off transitions across scans correctly,
and the store and the workflow are right; this is a stale notice, not a wrong result, and the
final-round bar for pushing was a defect that materially costs the user. Recorded as **O.10** below
rather than fixed, so a human decides whether it is worth a commit.

*(Round 7 decided: fixed. See below.)*

## One defect PR #17's round-7 review found and fixed

Round 7 opened a fresh loop and read round 6's own work cold. It re-measured the round-6 fix rather
than trusting it, then deliberately spent its remaining attention away from `MailFolderReader`,
which five rounds had already concentrated on.

**Round 6's fix was confirmed complete, not merely present.** Truncating five different message
shapes at *every one of their byte offsets* and running the production parse path — the
multipart/mixed text+PDF invoice round 6 measured, plus a bare `text/plain`, a
`multipart/alternative`, a `message/rfc822` attachment and a deliberately corrupt base64 part —
produced **only `FormatException`, which `tryParseMessage` holds, and zero `NullReferenceException`
across all 1,791 truncation points**. The two follow-up questions it raised both answered cleanly:
a `TextPart` whose `Content` is null does *not* throw out of `message.TextBody`/`HtmlBody`, so the
attachment filter had no untreated sibling; and a *complete* attachment with a zero-length body is
the only real part the filter drops, which is right — there are no bytes to hand on either way.

One candidate was raised and then **dropped on measurement**: `CachedMessageCountTakenAt` is still
stored as a raw LiteDB `DateTime` while the watermark was moved to ticks, so the "as of ..." stamp
under *Count messages* looked like it should show a UTC instant formatted as local time. Measured
against a real LiteDB file, it does not: LiteDB normalises to UTC on write and converts to
`DateTimeKind.Local` on read, so `DateTime.UtcNow` written at `2026-08-22 20:51` UTC came back as
`2026-08-23 06:51` Local and formats identically to `DateTime.Now`. The round-2 watermark defect was
the *comparison* breaking on that conversion, not the value being wrong; a display-only timestamp is
unaffected. No change made.

1. **The "selection was cleared" notice stayed on screen after the user answered it** — recorded by
   round 6 as **O.10** and left for a human, because round 6 was under a final-round materiality
   bar. Round 7 was not, and judged it worth closing: `selectionClearedCval` was written only by
   `scanForAccounts`, so after a scan cleared a vanished selection the `Severity.Warning` alert
   reading "...the selection has been cleared. Choose an account below." stayed up while the row
   the user had just ticked sat beneath it — two answers to the same question on the same screen,
   until some later scan happened to clear nothing.

   Fixed in `MailAccountsBrowserModuleCreators` by giving `write` an `onSuccess` callback and having
   `SelectAccount` pass one that lowers the flag. Only the success path clears it — a select that
   failed has answered nothing — and `scanForAccounts` still sets the flag from *every* scan, so a
   scan that clears again puts the notice straight back up. `CountMessages` and `ClearWatermarks`
   pass `ignore`; they do their own `transact`, so a write with nothing to say costs no transaction.

   Two red-first tests, both failing against `f384587` for exactly the predicted reason:

   | Level | Test | Red against `f384587` |
   | --- | --- | --- |
   | Unit | `SelectAccount takes the cleared-selection notice down, and a failed one leaves it up` | `Assert.False() Failure — Expected: False, Actual: True` |
   | E2E | `choosing an account takes the cleared-selection notice down` | `Assert.DoesNotContain() Failure: Sub-string found` — the notice was still rendered |

   The unit test also pins the half the fix must *not* do: a failed select leaves the notice up and
   reports its own message. The E2E test drives the real stack — module creator → `MailAccountApi` →
   workflows → the LiteDB store on a temp file → rendered markup — selecting from the maildir
   fixture profile, moving the root to the measured-shape profile so the selection is reconciled
   away, and then choosing one of the accounts that scan *did* find. Nothing is logged for either
   transition.

## One defect PR #17's round-8 review found and fixed

Round 8 read round 7's own work cold — nothing had reviewed it — and then spent the rest of its
attention where six rounds had not: the account reader, the store and its mappers, the ApiFactory's
error translation, the UI, and whether the change folder still agrees with what was built.

**Round 7's own work held up on re-measurement.** Its `onSuccess` callback on
`MailAccountsBrowserModuleCreators`' `write` is correct for every caller, not only `SelectAccount`:
`CountMessages` and `ClearWatermarks` answer nothing the notice asks, so `ignore` is right for both;
the flag is lowered only inside the `Ok` branch, so no failure path can take the notice down; and
the failed-then-successful sequence is pinned by round 7's own unit test. The shape is within
CLAUDE-project.md → *UI* rather than around it — the callback's only act is a `transact` on a `cval`
the module creator owns, the write still reloads, and `startWork` is untouched. Its two judgement
calls were re-checked and both stand: `CachedMessageCountTakenAt` is written by `DateTime.UtcNow`
and read back as `DateTimeKind.Local` for the *same instant* (`ThunderbirdStoreTests` already asserts
`storedAt.ToUniversalTime() = takenAt`), so `{takenAt:g}` renders the right local time and there was
nothing to fix; and **O.12** genuinely belongs in change #4, because `MailFolderReader.read` is
reachable from tests only — `grep` finds no call site in `MailAccountApiFactory` or anywhere else in
production — so nothing it returns can be wrong for a user today.

1. **Counting messages for a configured-but-missing account answered "0" instead of saying the store
   was gone.** Discovery deliberately keeps such an account rather than dropping it
   (requirements.md → *Reading the profile*) and the table marks it "Configured, but its store
   directory is missing". `MailFolderEnumerator` then gives it no folders — there is no directory to
   enumerate — so `MailFolderReader.countMessages` folded over an empty folder list and returned
   `Ok 0`, and `read` returned `Ok []`. That is exactly what a real, empty account returns, so the
   two were indistinguishable: the user pressed *Count messages* on the row labelled "its store
   directory is missing" and the same row then read **`0 (as of 23/08/2026 07:15)`** — two
   contradictory answers on one line, the second of them a number with nothing to suggest doubting
   it. `MailAccountApiFactory` also cached that zero, so it survived to the next page load.

   The area's own error DU has carried the case for this since the first commit and nothing ever
   raised it: `StoreDirectoryMissing of MailAccountId * path` was declared in
   `MailAccountsTypes.fs`, mapped to a sentence in `MailAccountApiMappers` ("The store directory for
   account '…' does not exist: '…'"), and asserted in `MailAccountApiMappersTests` — while no
   production line constructed it. `grep StoreDirectoryMissing` over the whole repository returns
   the declaration, the mapper, its two tests and two design.md mentions, and no producer.

   Fixed with one shared `MailFolderReader.accountWithReadableStore`, used by both `read` and
   `countMessages`: the account is looked up as before, and a store directory that is not on disk is
   reported rather than counted. Checked **live** with `Directory.Exists` rather than read off the
   account's `StoreDirectoryExists` flag, because that flag is a snapshot from the last scan and the
   likely real case is a drive unplugged or a folder deleted *between* scanning and counting, which
   a snapshot cannot see — and because the message is written in the present tense. `read` gets the
   same guard rather than only `countMessages`: it is the identical silent-wrong-result from the
   other end, and change #4 is the consumer that would have inherited it.

   Measured against `faecbd3`:

   | | Before | After |
   | --- | --- | --- |
   | `countMessages` (fixture's `imap.delta.example.com`, declared by `prefs.js`, no directory) | `Ok 0` | `Error (StoreDirectoryMissing (id, "…\ImapMail\imap.delta.example.com"))` |
   | `read` (same account) | `Ok []` | the same `Error` |
   | The page (E2E, *Count messages* on the Delta Mail row) | the cell showed `0 (as of …)` | a `MudAlert` naming the missing directory; the cell stays "Not counted yet" |
   | An account whose store IS there and holds nothing | `Ok 0` | `Ok 0` — unchanged |
   | A neighbouring account whose store is there | counted | counted; the guard refuses the one account, not the page |

   Red first at Integration (`countMessages reports a store directory that is gone rather than
   counting it as zero` and its `read` twin, both failing with `Expected
   Error(StoreDirectoryMissing _)` against `faecbd3`) and at E2E (`Assert.Contains() Failure:
   Sub-string not found` — the page never said the directory was gone). A third Integration test
   pins the half the guard must **not** swallow: a store directory that exists and holds no folders
   still counts zero. **No Unit-level test:** the guard's decision is a filesystem fact, and this
   repository's rule puts anything touching the filesystem at Integration — stated here rather than
   claimed as covered. Contract coverage was added after the fix, not red-first: `CountMessages` and
   `ReadMailFolder` are published dependency types, so both their real adapter and their in-memory
   fake now answer the missing-store case in the same suite, and the fake was taught the new shape
   so it cannot drift.

2. **The suite total had drifted three ways again** — the same defect round 4 filed as its finding 2.
   `CLAUDE-project.md` said 1040, this file's *Gate* section and its per-round table still said 1038
   with no round-7 column, and `tasks.md` **11.2** still carried round 5's 1035. All three are back
   in step at the measured 1050. No behavioural change, so no red-first test.

## Two defects PR #17's round-9 review found and fixed

Round 9 read `a3a6edd` cold. Eight rounds had concentrated on `MailFolderReader` and, latterly, on
the readers' entry conditions; this one re-measured round 8's guard and then spent its attention on
the top boundary mapper and the screen, which had had the least.

**Round 8's own work held up on re-measurement.** `accountWithReadableStore` is correct where it
matters and its two judgement calls stand. Checking `Directory.Exists` live rather than reading the
account's `StoreDirectoryExists` flag is right: the flag is a scan-time snapshot, the failure the
guard is written for (a drive unplugged, a folder deleted) happens after the scan, and the message
is in the present tense. An account whose store IS there and holds no folders still answers `Ok 0`,
which is the half the guard must not swallow and which round 8 pinned with its own Integration test.
`StoreDirectoryExists` is **not** dead — the table still disables the tick and prints "Configured,
but its store directory is missing" from it — and the two can legitimately disagree, in the
direction that makes the page more honest rather than less. Two limits are real but not worth
widening the DU for: `Directory.Exists` answers `false` for a path that is a file, an unreadable
directory or a broken junction as well as for one that is absent, so all four arrive as
`StoreDirectoryMissing`; and a directory that disappears *between* the guard and the read degrades
to the old `Ok 0` / `Ok []` in that window. Both are strictly smaller than the defect the guard
closed. Skipping the Unit level there was justified: the whole of the decision is
`Directory.Exists`, this repository puts anything touching the filesystem at Integration, and the
only separable part (`lookupAccount` returning `None`) is a path that predates the guard.

1. **Two profiles' copies of one account render as the same row twice.** requirements.md asks
   twice for this and neither ask had any behaviour behind it: *Walking the chosen folder* — "WHEN
   several profiles are found THE SYSTEM SHALL list all of their accounts, **qualified by the
   profile path they came from**, so two profiles containing the same account are distinguishable
   (Q4.9)" — and *Edge cases* — "WHEN two profiles declare accounts with the same email address THE
   SYSTEM SHALL list both, **qualified by profile path**."

   `ProfilePath` is carried the whole way and then dropped at the last hop.
   `DiscoveredMailAccount` has declared it since the first commit, `ThunderbirdAccountReader` fills
   it, `DiscoveredAccountEntity` persists it, `ThunderbirdEntityMappers` maps it both ways and
   `ThunderbirdPersistedShapeTests` asserts its stored field name — and then
   `MailAccountApiMappers.toMailAccountUiType`, the top boundary mapper, left it out, so
   `MailAccountUiType` never had it and the screen could not see it. The unit test covering that
   mapper is named `toMailAccountUiType carries every field` and did not carry this one.

   The chosen folder holding a live profile **and a backup copy of it** is one of the three shapes
   the walk is explicitly required to handle (requirements.md → *Walking the chosen folder*), so
   this is the ordinary case rather than a contrived one. Both rows then carry the same display
   name, the same address, the same format, the same folder count and the same size; the only
   thing separating them is an `Id` the table does not render. The user is being asked to tick one
   of them for import, and change #4 reads mail from whichever they tick — so picking the backup
   instead of the live profile is silently possible, and nothing on the page could have told them
   apart.

   Fixed by carrying `ProfilePath` through `MailAccountUiType` and the top mapper, and printing it
   under the account's name in the table, beside where "Configured, but its store directory is
   missing" already goes.

   | Level | Test | Red against `a3a6edd` |
   | --- | --- | --- |
   | E2E | `two profiles declaring the same account are told apart on the page by the profile they came from` | `Assert.Contains() Failure: Sub-string not found` — both accounts listed, both named "Duplicated Mail", neither profile path anywhere in the markup |
   | Unit | `toMailAccountUiType keeps two profiles' copies of one account apart` | the field did not exist |
   | Unit | `toMailAccountUiType carries every field including a cached count` | extended to assert the field it was named for |
   | Contract | `every account GetAccounts returns names the profile it was discovered in` (real API **and** fake) | the field did not exist |

   No new Integration test: nothing about storage changed — `ProfilePath` already round-trips
   through `ThunderbirdStore` and is already asserted there and in the persisted-shape suite.

2. **A test whose setup could silently not happen.** `ThunderbirdFolderScannerTests`' "scan records
   an unreadable directory and continues the walk" denies itself access to a directory by spawning
   `icacls`, and never looked at its exit code. If the deny does not apply, the directory is
   readable, the walk correctly records nothing, and the test fails on its scanner assertions — so
   a setup that did not happen is indistinguishable from a defect in the scanner. That is precisely
   how it read when a full-suite run of this round hit it once. Both copies of that setup (the
   scanner test and the E2E walk test) now assert `icacls` exited 0, naming the setup as the
   failure when it is. No behavioural change, so no red-first test.

## One defect PR #17's round-10 review found and fixed

Round 10 read `0071d5c` cold — nothing had reviewed it. It re-checked round 9's own work and then
spent its attention on the areas nine rounds had touched least: `ThunderbirdAccountReader` and
prefs.js parsing, `ThunderbirdStore`, the factory's error translation in both directions, and the
four domain workflows.

**Round 9's own work held up.** Both boundary mappers were re-checked field-for-field in both
directions and are now complete: `ThunderbirdEntityMappers` maps all nine `DiscoveredMailAccount`
fields (`Folders` deliberately excepted — it lives in its own collection and is supplied by
`ThunderbirdStore`'s join), all four `MailFolder` fields, and all three `FolderWatermark` fields;
`MailAccountApiMappers` maps all nine `MailAccountUiType` fields, all four `MailFolderUiType`
fields, and all four `DiscoveryResultUiType` fields. The new Contract row does run against the real
API *and* the fake. The `ProfilePath` caption renders safely for an empty or missing path (a
`MudText` caption reading "Profile: ") and wraps for a long one.

Two things that look like the same defect were checked and are **not**: `NoAccountSelected` is
declared, mapped and unit-tested here with no production line constructing it — but
`docs/changes/invoice-extraction/design.md` and its `tasks.md` construct it, the same deliberate
forward declaration as `ReadMailFolder`, and requirements.md's "so a *later* scan can refuse" says
so. And `CachedMessageCountTakenAt` is written as `DateTime.UtcNow` into a LiteDB `DateTime?`
column, which round 3 established comes back as `DateTimeKind.Local`; the *instant* survives, so
`$"{takenAt:g}"` renders the correct local wall-clock time, and `ThunderbirdStoreTests` already
pins that with `Assert.Equal(takenAt, storedAt.ToUniversalTime())`.

1. **A scan silently discarded a message count the user had waited minutes for.**
   design.md → *Decisions taken #4* puts a header pass over the real profile at *minutes*, which is
   the whole reason the count is a user-triggered action cached with its timestamp rather than
   something the page computes. But a scan re-reads `prefs.js` and re-enumerates folders; it never
   counts messages, so `ThunderbirdAccountReader` reports every account with
   `CachedMessageCount = None` — and `ScanForMailAccountsWorkflow` handed that straight to
   `saveMailAccounts`, which "replaces the previous set of accounts and folders rather than
   accumulating". The figure was therefore thrown away for an account the scan had **just found
   again**, and the column went back to "Not counted yet" with nothing on screen to say why.

   The two buttons sit on the same page, so the cheap one silently undid the expensive one:
   *Scan for accounts* is the ordinary way to pick up a folder added in Thunderbird, and pressing it
   cost the user another minutes-long pass with no warning. requirements.md's "WHEN a cached message
   count is displayed THE SYSTEM SHALL state when it was taken, because the count is a snapshot and
   the mailbox keeps growing" only means anything if a stale count survives to be stated — the
   timestamp exists precisely so an old reading is still worth showing, and discarding the reading
   removes the thing the timestamp is for. The codebase already treats the count as independent of
   the scan-replace cycle: `ThunderbirdStore.updateCachedMessageCount` exists solely to update it
   *in place*, "leaving its folders untouched — unlike `saveMailAccounts`, which replaces the whole
   set".

   Fixed in the workflow, not the store: this is a decision about reconciling stored state against
   fresh discovery, which is exactly what `reconcileSelection` beside it already is, and it is
   unit-testable with lambdas. `scanForMailAccounts` takes `LoadMailAccounts` as a new leading
   dependency and a private `carryCachedCounts` copies the count forward **by id, and only the
   count** — every other field is the fresh scan's answer, an account the scan no longer finds is
   not resurrected, and a discovery that somehow arrived with a count of its own keeps it. The
   returned `DiscoveryResult.Accounts` carries the counts too, so what the page is handed agrees
   with what was stored.

   Red first at Integration (`a rescan keeps the cached count of an account it finds again` —
   "The rescan discarded the cached message count of an account it found again"), plus three Unit
   tests on the workflow (the count carried with every other field asserted as the *fresh* one; a
   stored account the scan no longer finds not resurrected; the store's error returned with nothing
   saved), a Contract row on `MailAccountApi`, and an E2E flow asserting the rendered
   "4 (as of …)" survives the second scan with the same timestamp.

   **The Contract row caught the fake drifting**, which is what the shared-suite rule is for: the
   in-memory fake rebuilt its ten accounts on every `ScanForAccounts`, so it discarded the count the
   same way the real API used to. It failed red (`Expected: Some((4, …))  Actual: null`) while the
   real API passed, and now applies the same carry-forward rule.

## The intermittent failures, measured

Round 9 measured the flake rate directly rather than describing it, 30 full-suite runs in all:

| Tree | Runs | Failed runs | Cause |
| --- | --- | --- | --- |
| `a3a6edd` (before this round) | 14 | 1 | the LiteDB `BsonMapper.Global` race |
| with this round's fix | 16 | 3 | two confirmed the same race; one was the `icacls` setup above |

Every `BsonMapper` failure carries the same signature — `System.InvalidOperationException:
Collection was modified; enumeration operation may not execute` out of `BsonMapper.SerializeObject`,
raised at `ThunderbirdDatabaseContextModule.fs:16`, the warm-up line — and lands on whichever test
happened to construct a context first. It is the pre-existing, documented race (CLAUDE-project.md →
*Per-integration databases*), not anything this change introduced, and closing it needs a
process-wide lock and its own change folder. **The rate is unchanged by this round**: the difference
between 1-in-14 and 3-in-16 is well inside the noise for samples this size, and two of the three
are that same race.

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
  span. Rounds 2, 3 and 4 added and then correctly sized a guard so an over-large folder is
  reported rather than crashing or counting zero (round 2 finding 4, round 3 finding 2, round 4
  finding 1), but the requirement itself needs a streaming line reader that keeps exact byte
  offsets. That is a design change, not a defect fix, and wants its own change folder. **Until it
  lands, an account holding a folder over `MailFolderReader.MaxBufferableBytes` — 1,073,741,791
  bytes, about 1.0 GiB — cannot be counted or read at all**, and *Count messages* fails for the
  whole account rather than under-reporting it. Rounds 2 and 3 stated that threshold as "~2 GiB";
  it is half that, because the span becomes a string and a .NET string cannot exceed the 2 GB
  object-size ceiling. The measured profile has a 2.5 GB mbox, so this is not hypothetical at
  either figure.
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
  it belongs to that change rather than to this one. **Round 5 adds one more thing to surface
  there**: `tryParseMessage` discards a segment MimeKit cannot parse (round 5, finding 1) and, like
  a torn message before it, says nothing about having done so. Both are the same gap — a per-read
  diagnostic channel this change's `ReadMailFolder` signature has no room for — and both close
  together when change #4 widens it.
- **O.8** `MailAccountApiFactory` binds `loadWatermark` and `saveWatermark` but nothing uses them
  yet — `MailFolderReader.read` is not wired into `MailAccountApi`, because reading mail is change
  #4's surface. Harmless today, and the bindings are what change #4 will attach `ReadMailFolder`
  to; noted so it is not mistaken for live wiring.
- **O.9** *(found by PR #17's round-4 review; not previously recorded here.)* **Three prefs.js
  fields requirements.md asks for are neither read nor stored.** requirements.md → *Reading the
  profile* says an account SHALL take its "type, hostname, username, display name and store
  format" from `mail.server.<server>.*` and "every identity's email address **and full name**"
  from `mail.identity.<id>.*`. `ThunderbirdAccountReader` reads `name`, `hostname` (only as a
  fallback when `name` is absent), `storeContractID`, `directory-rel` and `useremail` — the server
  `type`, the server `userName` and the identity `fullName` are never read. That is not an
  oversight of the implementation so much as of the design: design.md's own
  `DiscoveredMailAccount` has no field for any of the three, so the type had nowhere to put them
  while its data-flow diagram still listed them. Nothing in this change or in change #4's stated
  surface consumes them, so adding fields no caller reads would be speculative; the honest record
  is that these three SHALLs are unmet, and the change that first needs a server type, a login
  name or a sender's display name adds them to `DiscoveredMailAccount`, to
  `DiscoveredAccountEntity`, and to both boundary mappers together.
- **O.10** *(found by PR #17's round-6 review; **closed by round 7** — see the round-7 section
  above.)* The "selection was cleared" notice came down on the next scan rather than when the user
  answered it. `SelectAccount` now lowers the flag on success, with a Unit and an E2E test pinning
  both halves of the behaviour.
- **O.11** *(found by PR #17's round-6 review.)* **An mbox message that is half-written but has its
  header/body separator is consumed rather than re-read.** `readMboxFile`'s torn test asks only
  whether the last segment has a header/body separator. A message whose top-level headers and blank
  line are on disk but whose MIME body is still being flushed passes that test, is parsed as
  complete, and the offset advances past it — so the finished version is never re-read, and with
  **O.10**'s sibling fix in place the attachment that arrives a moment later is permanently absent
  from that folder's messages rather than merely absent from this scan. This predates round 6 (it
  applies to a truncated plain-text body just as much) and round 6's fix converts a crash into it
  rather than causing it. Making the torn test structural — "does this segment parse into a complete
  MIME tree" — would close it, at the cost of a folder whose last message is *permanently*
  malformed re-reading the same bytes on every scan; that trade wants its own change folder rather
  than a review-fix commit.
- **O.12** *(found by PR #17's round-7 review.)* **An attachment that is not a `MimePart` is
  silently dropped.** `parseMessage` maps `message.Attachments` with `| :? MimePart as part ... | _
  -> None`, so a `message/rfc822` attachment — a forwarded email carried as `MessagePart`, which
  derives from `MimeEntity` and *not* from `MimePart` — is skipped without a trace, even though it
  has a `Content-Disposition: attachment`, a filename and bytes. requirements.md → *Reading
  messages* says "WHEN a message carries attachments THE SYSTEM SHALL return each attachment's
  filename and its bytes", so this is a stated SHALL that is unmet, and it is the silent-wrong-
  result shape: the message reports `Attachments = []`, indistinguishable from carrying none. It
  predates round 6's filter rather than being caused by it (the `:? MimePart` test is from the
  original commit) and costs nothing today, because `MailFolderReader.read` is not wired into
  `MailAccountApi` at all — see **O.8**. Not fixed here on purpose: serialising a nested message
  back to bytes and synthesising a filename for it is *new behaviour* with its own encoding and
  naming decisions, not a defect fix, and change #4 is where an actual attachment consumer exists to
  specify it against. It closes with **O.7** in that change, next to the diagnostic channel the same
  signature has to grow.
- **O.13** *(found by PR #17's round-8 review.)* **Enumeration treats every file that is not `.msf`
  as a folder's mbox, where requirements.md says "an extensionless file".**
  `MailFolderEnumerator.enumerateMboxLevel` excludes directories and `.msf` and accepts everything
  else, so a real profile's per-server sidecar files — `msgFilterRules.dat`, `popstate.dat`,
  `filterlog.html` — are listed as folders. The cost is a folder count and a "to scan" size that are
  both slightly too large on the accounts page, and a scan that opens a `.dat` file expecting mbox
  (it finds no `From ` boundary and contributes nothing, so nothing worse follows). Not fixed here
  because the obvious fix is wrong: Thunderbird lets a folder be named with a dot in it
  (`Amazon.co.uk`), so a literal "extensionless only" rule would silently drop a real mailbox — a
  worse defect than the one it closes. Getting it right means excluding Thunderbird's sidecars by
  name, which is a decision about what the integration knows of Thunderbird's on-disk conventions
  rather than a defect fix, and no committed fixture carries one to test against yet. The measured
  profile's manual run (10 accounts, 59 folders on the largest) did not separate real folders from
  sidecars, so re-measure when this is picked up.

## Stated plainly: maildir ships verified against synthetic fixtures only

Per Q4.11 — the measured profile is 100% mbox (`berkeleystore`), so there is no real maildir data
in this environment to verify the maildir reader against. `Fixtures/ThunderbirdProfiles/maildir-shape/`
is entirely synthetic, and `MailFolderEnumerator`'s and `MailFolderReader`'s maildir code paths are
tested only against it. This is a real, acknowledged coverage gap for maildir specifically, not an
oversight — restated here per CLAUDE.md's "no silently dropped level" rule, even though the level
itself (Integration) is present and green.
