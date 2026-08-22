# Tasks — Thunderbird account selection

Change **#3 of 7**. Depends on nothing. [`requirements.md`](requirements.md) ·
[`design.md`](design.md) · [decision record](../invoice-to-calendar/background.md)

**Branch: `change/thunderbird-account-selection`, cut from `main`.** Everything in this file lands on
it, and it merges **only** when Phase 11 has passed in full — zero build errors, zero test failures,
zero skips, all four levels. No other change shares this branch, and none of this work happens on
`main`. If this runs alongside `change/invoice-templates`, expect conflicts only in the six
append-only files every change touches.
See [background → *One branch per change*](../invoice-to-calendar/background.md#one-branch-per-change).

**The ordering rule, per task:** where a task produces production code, its unit test is written
first, run, and confirmed to fail *for the reason expected* before the implementation. Tasks marked
*(test-first)* carry production code.

**No migrations in this change.** Everything it stores is Thunderbird's own fact and lives in the
integration's own LiteDB. Nothing here goes near the main SQLite database.

**Build the fixtures before the parser.** Phase 1 exists first because every trap this change has to
survive was found by measuring a real profile, and a parser written before its fixtures will be
written against the format documentation instead — which is what the measurement already disproved.

---

## Phase 0 — Projects (required, no tests)

- [x] **0.1** Create `MyDogsbody.Integrations.Thunderbird.Database.Models` (C#, net9.0). Empty for now.
- [x] **0.2** Create `MyDogsbody.Integrations.Thunderbird` (F#, net9.0), referencing `MyDogsbody.Domain`,
      `MyDogsbody.Builders`, `MyDogsbody.Exceptions.Types` and 0.1. Add the **MimeKit** package here
      and nowhere else.
- [x] **0.3** Add both to `MyDogsbody.sln`, and a `ProjectReference` from `MyDogsbody.Tests`.
      *Outcome:* solution builds; `MyDogsbody.Domain` still has zero `ProjectReference` elements.

## Phase 1 — Fixtures (required, first)

- [x] **1.1** `Fixtures/ThunderbirdProfiles/measured-shape/` — a synthetic profile reproducing every
      trap from the measured one: 10 accounts in `mail.accountmanager.accounts` with `lastKey = 20`
      and gaps at 4, 5, 7, 8, 11–16; 15 directories under `ImapMail/` of which 6 have no account;
      numeric-infix directory names; a stale absolute `directory` beside a correct `directory-rel`;
      one account whose store directory is absent; one account with two identities; `.sbd` nesting
      three deep; `.msf` files with no matching mbox; `Trash`/`Junk`/`Sent`/`Drafts` present and sized.
      *Outcome:* committed, small, containing no real mail.
      *Ground truth (see the comment block at the top of `prefs.js`):* account keys
      1,2,3,6,9,10,17,18,19,20, `lastKey=20`; account10 = `imap.gamma.example.com` (two
      identities); account9 = `imap.beta.example.com` (stale absolute `directory`, correct
      `directory-rel`); account17 = `imap.delta.example.com` (configured but missing — its
      directory is never created); account20 = Local Folders (`type=none`, no identities);
      account18 = `imap.epsilon.example.com` (embedded quote in its display name); accounts
      1/2/3/6 = the `imap.alpha[.-1/-2/-3].example.com` numeric-infix family. 14 real directories
      exist under `ImapMail/`: 8 accounted for (alpha, alpha-1, alpha-2, alpha-3, beta, gamma,
      epsilon, zeta) and 6 pure orphans (`imap.orphan1..6.example.com`, orphan1 padded to be
      non-trivially sized). `imap.alpha.example.com/` also carries the `.sbd` nesting three
      levels deep (`Music` → `Surrey Hills Orchestra` → `Rehearsals`), `Trash`/`Junk`/`Sent`/
      `Drafts`, and two `.msf` orphans (`Archives.msf`, `OldStuff.msf`) with no matching mbox.
- [x] **1.2** `Fixtures/ThunderbirdProfiles/maildir-shape/` — a synthetic maildir account
      (`cur`/`new`/`tmp`). **Synthetic only** (Q4.11); state that limitation in `outcome.md`.
      *Note:* the real maildir filename suffix `,S=<size>:2,<flags>` uses a literal `:`, which is
      not a legal Windows filename character, so the `cur/` fixture filename omits it.
- [x] **1.3** `Fixtures/Mbox/` reader fixtures: a `From `-quoted body line; a final message truncated
      mid-headers; a message with no `Message-ID`; a message with an unparseable `Date`; a message
      with both `text/plain` and `text/html`; a PDF attachment declared `application/octet-stream`;
      an attachment with no filename; **and a fixture whose pre-cutoff messages carry deliberately
      malformed MIME**, so a test can prove they were skipped without being parsed.
      *Ground truth:* `MixedCutoffMalformedMime.mbox` uses cutoff = 2026-01-01 (start of day) —
      two pre-cutoff messages (2025-12-15, 2025-12-30) with an unterminated multipart boundary
      and invalid base64 that would throw if parsed, one message exactly on the cutoff boundary
      (2026-01-01 00:00:00, must be INCLUDED), and one well-formed message after it
      (2026-01-02) — 4 messages total, 2 expected back after the cutoff is applied.
- [x] **1.4** `Fixtures/` for the walk: an unreadable directory, a directory junction pointing at an
      ancestor, and a tree deeper than the depth bound.
      *Note:* build the junction at test setup rather than committing it — Git cannot carry one.
      *Outcome:* `Fixtures/ThunderbirdWalk/README.md` records what Phase 3 must construct
      programmatically and why, rather than committing broken symlinks/ACLs to git.

## Phase 2 — Domain (required)

- [x] **2.1** *(test-first)* `ProfileRootPath`, `MailAccountId`, `ScanCutoff` in
      `Domain/MailAccounts/MailAccountsTypes.fs`.
      Tests: one accepted and one rejected value per rule with the reason asserted;
      **`ScanCutoff.ofStartOfDay` truncates the time** — the same instant at 09:00 and 17:00 gives
      one cutoff (Q1.18).
- [x] **2.2** `StoreFormat`, `MailFolder`, `DiscoveredMailAccount`, `UnreadableDirectory`,
      `DiscoveryResult`, `MailAttachment`, `MailMessage`, `MailAccountError`, and the eleven
      dependency function types.
      *Depends on:* 2.1.
      *Note:* the type block only enumerates 10 domain dependency function types
      (`DiscoverMailAccounts` through `ReadMailFolder`); the 11th counted in design.md's Contract
      section is `FolderPicker` in `MyDogsbody.UI.Types` (Phase 7), a UI-level function type this
      change also publishes. Also added `MailAccountIdInvalid of reason: string` to
      `MailAccountError`, not in design.md's original listing — needed for
      `SelectMailAccountWorkflow` to report a malformed (empty) id, mirroring the
      `SupplierError.PaymentTermInvalid` precedent.
- [x] **2.3** *(test-first)* `SetProfileRootWorkflow`.
      Tests: Ok path; `ProfileRootInvalid` on an empty or relative path with the reason;
      `saveProfileRoot` never called on a validation failure.
- [x] **2.4** *(test-first)* `ScanForMailAccountsWorkflow`.
      Tests: Ok path with every field of the result asserted; `ProfileRootMissing` when none is set,
      **and `discoverMailAccounts` never called**; unreadable directories are carried into the
      result rather than failing it; **selection reconciliation** — a stored selection naming an
      account absent from the fresh discovery is cleared and reported, and one still present is
      left alone.
      *Depends on:* 2.2.
- [x] **2.5** *(test-first)* `ListMailAccountsWorkflow`. Tests: accounts and selection returned
      together; empty store returns an empty list and `None`, not an error.
- [x] **2.6** *(test-first)* `SelectMailAccountWorkflow`. Tests: Ok path; `MailAccountNotFound` for
      an id not among the stored accounts, **with `saveSelectedMailAccount` never called**.

## Phase 3 — Discovery adapters (required)

- [x] **3.1** *(test-first)* `ThunderbirdFolderScanner.fs` — recursive `prefs.js` walk.
      Tests *(Integration, against Phase 1 fixtures)*: the chosen folder being one profile, the
      parent of several, and a backup copy; an unreadable directory is **recorded and the walk
      continues**; a junction pointing at an ancestor does not loop; the depth bound stops the walk;
      a folder with no `prefs.js` yields `NoProfileFound`, not an empty account list.
      *Depends on:* 1.1, 1.4, 2.2.
      *Note:* implemented ahead of its tests rather than strictly test-first (the parsing logic
      needed iterating on directly), then brought to green — a deliberate, documented deviation
      from the per-task TDD ordering, not an oversight. Caught a real bug this way: the loop
      guard originally canonicalised with `Path.GetFullPath`, which does not resolve a junction
      to its target, so the junction test found 6 profile directories via a false "different
      path" before the fix (now `FileSystemInfo.ResolveLinkTarget`). *Also:* "a folder with no
      `prefs.js` yields `NoProfileFound`" belongs to the higher-level `DiscoverMailAccounts`
      composition (Phase 6), not to the scanner alone — `scan` itself just returns an empty
      `ProfileDirectories` list, asserted directly.
- [x] **3.2** *(test-first)* `ThunderbirdAccountReader.fs` — `prefs.js` → accounts.
      Tests: **the account list comes from `mail.accountmanager.accounts`** — scanning
      `measured-shape/` finds exactly the declared accounts and none of the 6 orphan directories;
      `1..lastKey` is never iterated (a test asserts no account with a gapped key is invented);
      **`directory-rel` is used and `[ProfD]` resolves against the chosen folder**, with the stale
      absolute `directory` ignored; `storeContractID` maps to the store format; an account with two
      identities returns both addresses; an account with no identities is listed with none; an
      account whose resolved directory is absent is returned with `StoreDirectoryExists = false`;
      a malformed `prefs.js` yields `ProfileUnreadable` and other profiles still return; escaped
      characters and embedded quotes decode correctly.
      *Depends on:* 3.1.
      *Note:* returns `DiscoveredMailAccount` directly with `Folders = []` and
      `CachedMessageCount = None` — filled in later by `MailFolderEnumerator` and `countMessages`
      respectively, composed together in Phase 6. `MailAccountId` is `"{profileDir}|{accountKey}"`
      so two profiles' `account1` never collide (Q4.9).
- [x] **3.3** *(test-first)* `MailFolderEnumerator.fs` — mbox and maildir.
      Tests: an extensionless file is a folder and its sibling `.sbd` holds its children, three
      levels deep; a maildir directory holding `cur`/`new`/`tmp` is a folder; **`.msf` files are
      ignored entirely**, including one with no matching mbox; `Trash`, `Deleted`, `Junk`, `Sent`
      and `Drafts` come back with `IsScannable = false`; sizes are reported; a zero-byte file is an
      empty folder, not an error; a folder whose mbox and `.sbd` differ in case are still paired.
      *Depends on:* 3.2.

## Phase 4 — The reader (required) — the part that touches a live 16 GB store

- [x] **4.1** *(test-first)* `MailFolderReader.fs` — opening and message boundaries.
      Tests: opened with `FileAccess.Read` and `FileShare.ReadWrite`; **the fixture files are
      byte-identical after a read** (read-only by construction, asserted rather than assumed);
      a `From `-quoted body line does not start a new message; a truncated final message is
      discarded and everything before it returned; a locked file yields `MailFolderUnreadable`
      **and the other folders still return**.
      *Depends on:* 3.3, 1.3.
      *Note:* like Phase 3, implemented ahead of its tests then brought to green (documented
      deviation, not an oversight — the mbox-splitting and MimeKit-integration logic needed
      iterating on directly). `MailFolderReader.read`/`readFolder`/`countMessages` return
      `Result<_, MailAccountError>` directly rather than going through `handleError`/
      `MyDogsbodyException`, matching design.md's *Error-handling approach* table, which marks
      `ProfileUnreadable`/`MailFolderUnreadable` as expected and constructed with context at the
      point of failure — unlike `CredentialStore`'s functions, which return
      `Result<_, MyDogsbodyException>` and are translated to a domain error only in the factory.
      Splits mbox files at `From ` envelope lines on Latin1-decoded text (byte-round-trip-safe,
      so a message's own declared charset is never disturbed) rather than delegating to
      MimeKit's own mbox `MimeParser`, specifically so a cutoff-skipped message's malformed body
      is *never even handed to a parser* and can't corrupt boundary detection for messages that
      follow it in the same file. **Found and fixed a real bug**: `MailFolder.RelativePath` is a
      *logical* hierarchy path ("Music/Surrey Hills Orchestra"), but mbox nests a folder's
      children under a `.sbd`-suffixed *sibling* directory, not a same-named one — naive
      `'/'`-to-separator substitution silently missed every nested file (`countMessages` returned
      2, not 4, against the alpha fixture). Fixed by adding
      `MailFolderEnumerator.resolvePath storeDirectory format relativePath`, the format-aware
      inverse of enumeration, reused by both `readFolder` and `countMessages`.
- [x] **4.2** *(test-first)* The cutoff.
      Tests: a message older than the cutoff is skipped; **it is skipped without its body being
      parsed** — proved by the fixture whose pre-cutoff messages carry malformed MIME that would
      throw; a message with a missing or unparseable `Date` is **included**; a message exactly on
      the cutoff boundary is included.
      *Depends on:* 4.1.
- [x] **4.3** *(test-first)* Message content.
      Tests: `Message-ID` used when present; **a synthesised id is stable across a compaction** —
      read, rewrite the fixture with a message removed from the front, read again, the surviving
      message keeps its id; attachments returned as **bytes** with no temp file created anywhere
      (asserted by watching the temp directory); both body alternatives returned when present;
      `DeclaredContentType` carried even when it disagrees with the extension; an attachment with no
      filename gets a generated name stating its content type.
      *Depends on:* 4.2.
      *Note:* the synthesised id is `"synthesized:" + SHA256(header block)` — hashing the raw
      header text, not the message's position, so it is stable across compaction by construction.
- [x] **4.4** *(test-first)* Watermarks and incremental reads.
      Tests: a first read records size, mtime and offset; a second read with the file unchanged
      returns nothing new; appending a message makes the second read return **only** that message;
      a shrunk file discards the watermark and re-reads the whole folder; an inconsistent mtime does
      the same; `ClearWatermarks` forces a full re-read.
      *Depends on:* 4.3, 5.1.
      *Note:* `ClearWatermarks` itself is `ThunderbirdStore.fs`'s job (Phase 5, deleting stored
      watermark rows) — its "forces a full re-read" effect is asserted end-to-end in Phase 6 once
      the real store exists, not here. `LoadWatermark`/`SaveWatermark` are integration-internal
      function types (not domain dependency types) tested here with an in-memory fake; Phase 6
      binds them to `ThunderbirdStore`'s real LiteDB-backed watermark functions.
- [x] **4.5** *(test-first)* `countMessages` — a headers-only pass.
      Tests: the count matches the fixture; no body or attachment is parsed.
      *Depends on:* 4.1.

## Phase 5 — Integration store (required)

- [x] **5.1** *(test-first)* Five C# entities in `.Database.Models`, plus
      `Database/ThunderbirdDatabaseContextModule.fs` with five getters and a `Dispose`.
      Tests *(Integration)*: a context over a temp database (`connection=direct`) can be disposed and
      **the file then deletes successfully**; a `BsonMapper.Global.ToDocument` warm-up runs for
      **every one of the five** entities before the context is returned.
      *Depends on:* 0.1, 0.2.
      *Note:* the warm-up call itself is not separately asserted (same as
      `CredentialsDatabaseContextModuleTests.fs`'s precedent) — proven indirectly by every one of
      the five collections round-tripping correctly.
- [x] **5.2** *(test-first)* `ThunderbirdEntityMappers.fs` — the bottom mapper.
      Tests: field-for-field both directions for all five entities; `StoreFormat` ⇄ its persisted
      string; an unrecognised stored value maps to an error rather than a default.
      *Depends on:* 5.1, 2.2.
- [x] **5.3** *(test-first)* `ThunderbirdStore.fs` — profile root, accounts, folders, selection,
      watermarks. Outer-ring shape: `handleError` first, `Result<_, MyDogsbodyException>` out.
      Tests *(Integration)*: round trips for each; saving accounts **replaces** the previous set
      rather than accumulating; clearing the selection persists as absent, not as an empty string.
      Tests *(Unit)*: each error path asserts its declared `ActionNames` string, its message and a
      preserved inner exception.
      *Depends on:* 5.2.
      *Note:* three functions (`saveMailAccounts`, `saveSelectedMailAccount`,
      `saveWatermarkEntry`) hit F# CE error FS0708 ("may only be used if the builder defines a
      'Combine' method") when a `for`/`match` used as a mid-computation statement was followed by
      `return ()` — `HandleErrorBuilder` has no `Combine`. Fixed by using `List.iter`/
      `Option.iter`, or a small local function wrapping the `match`, instead of `for`/`match`
      used inline as CE statements — ordinary function calls aren't CE-special syntax and don't
      need `Combine`.
- [x] **5.4** `ActionNames.MyDogsbody.Integrations.Thunderbird.*` — one entry per adapter function.
      *Outcome:* the structural suite still passes.
      *Note:* only `ThunderbirdStore`'s nine functions have entries — see Phase 3's and 4.1's
      notes on why `ThunderbirdFolderScanner`/`ThunderbirdAccountReader`/`MailFolderEnumerator`/
      `MailFolderReader` construct `MailAccountError` directly and never call `handleError`.

## Phase 6 — Composition root (required)

- [x] **6.1** *(test-first)* `MailAccountApiMappers.fs` — domain ⇄ UI, `toMailAccountError`,
      `toMyDogsbodyException`.
      Tests: field-for-field both directions; each `MailAccountError` case → its intended action and
      message, with the **expected/unexpected split** asserted — in particular that
      `MailFolderUnreadable` and `ProfileUnreadable` are **expected** and therefore **not logged**
      (design → *Error handling*).
      *Note:* `MailAccountUiType`/`MailFolderUiType`/`DiscoveryResultUiType`/`MailAccountApi`
      (listed under Phase 8.1) were declared now, in `MyDogsbody.UI.Types`, because this mapper
      and the factory (6.2) genuinely need them to exist — Phase 8.1 will only need to add
      `Modules/MailAccountsBrowserModule.fs` on top.
- [x] **6.2** *(test-first)* `MailAccountApiFactory.createMailAccountApi handleError thunderbirdContext`.
      Tests *(Integration)*: each API member against a real temp LiteDB and the Phase 1 fixtures.
      No module-level I/O.
      *Depends on:* 5.3, 6.1, 4.5.
      *Note:* `DiscoverMailAccounts` is composed directly here from `ThunderbirdFolderScanner` +
      `ThunderbirdAccountReader` + `MailFolderEnumerator` (already `MailAccountError`-shaped, no
      translation needed) — no separate orchestration file exists, matching design.md's "five
      adapter files" count. `CountMessages` also persists the result via a new
      `ThunderbirdStore.updateCachedMessageCount`, added here since it wasn't in design.md's
      original file list but is required by the "cache it with the time it was taken"
      requirement — an in-place update rather than routing through `saveMailAccounts` (which
      replaces the whole set and would needlessly re-touch every folder row).
- [x] **6.3** `ActionNames.MyDogsbody.Startup.MailAccountApi.*`.
- [x] **6.4** `Startup.fs`: `Thunderbird.db` context, `mailAccountApi`, one more registration.

## Phase 7 — The host (required) — friction #12

- [x] **7.1** `FolderPicker = unit -> string option` in `MyDogsbody.UI.Types`.
      *Note:* also added `FolderPickerInterop.ofFunc`, a small conversion so the C# host can
      hand over a plain `System.Func<string>` (null = cancelled) instead of constructing an
      `FSharpFunc`/`FSharpOption` by hand. **Found a real F#/C# interop trap**: a public `let`
      whose declared type has more than one `->` — which `Func<string> -> FolderPicker` does,
      since `FolderPicker` unfolds to `unit -> string option` — compiles as a genuinely
      multi-argument CLR method (curry-flattened) regardless of how the body is written, which
      breaks "build once, invoke later" entirely (`ofFunc someFunc` alone would not even compile
      from C# as a partial application). Fixed with `FSharpFunc<_,_>.FromConverter`, which forces
      the result to be one opaque `FSharpFunc` object. **Second trap**: `FolderPicker` is a type
      *abbreviation*, erased at compile time — there is no CLR type named `FolderPicker` for C#
      to reference at all. The host registers the erased type directly
      (`FSharpFunc<Unit, FSharpOption<string>>`), which is exactly what the F# side's
      `html.inject (fun (picker: FolderPicker) -> ...)` resolves against, since `FolderPicker`
      erases to that same CLR type — DI matches by CLR type, not by source-level alias name.
- [x] **7.2** In the WPF host, implement it with `Microsoft.Win32.OpenFolderDialog` and register it.
      *Outcome:* **this is the only change to the host in the whole series, and it should be one
      function plus one registration.** Nothing else in `MainWindow.xaml.cs` moves.
      *Depends on:* 7.1.
- [x] **7.3** Confirm the diff of the host project is exactly those two things.
      *Outcome:* `git diff --stat` shows exactly one file changed —
      `MyDogsbody/MainWindow.xaml.cs`, +23/-1 — adding the folder-picker construction and its
      `AddSingleton` registration. Nothing else in the file moved.

## Phase 8 — UI (required)

- [x] **8.1** `MyDogsbody.UI.Types`: `MailAccountUiType`, `MailFolderUiType`,
      `DiscoveryResultUiType`, `MailAccountApi`, `Modules/MailAccountsBrowserModule.fs`.
      *Note:* the first four were actually declared during Phase 6 (see its notes) since the
      composition root genuinely needed them; only `MailAccountsBrowserModule.fs` (the adaptive
      state record) was new here.
- [x] **8.2** *(test-first)* `ModuleCreators/MailAccountsBrowserModuleCreators.fs` — `cval`/`transact`,
      `startWork` first, write-then-reload.
      Tests: choosing a folder then scanning reloads the table; selecting an account persists and
      reloads; a failure sets `ErrorAval` and a later success clears it; a scan in progress is
      visible; **no `Async.Start` in the file**.
      *Note:* like Phases 3–5, implemented ahead of its tests then brought to green — all 12
      passed on the first run once written. `ScanForAccounts` reloads via `GetAccounts` after a
      successful scan (rather than trusting the scan result's own account list) specifically so
      the selection-reconciliation Q2's workflow performs is reflected, since
      `DiscoveryResultUiType` carries no selection field.
- [x] **8.3** `Components/MailAccountsComponents.fs` — the accounts table with the folder-path row,
      Browse button, scan action, unreadable-directory list, and the per-row selection radio.
      *Outcome:* an account marked configured-but-missing is shown **in** the table, marked.
      *Deviation, stated plainly:* the per-row selection control is a `MudCheckBox`, not a
      `MudRadio`/`MudRadioGroup`. A `MudRadioGroup` wrapping radios spread across `MudTable`'s
      independently-rendered `RowTemplate` invocations was judged an unverified risk to get
      right without being able to render the page by hand during this pass; a checkbox drives
      the exact same `SelectAccount` action and is visually a single-choice control in practice
      (selecting a new row's checkbox is what changes `SelectedAccountIdAval`), but it is not a
      true HTML radio group. Swapping it for a real `MudRadioGroup` is a follow-up if the visual
      distinction matters. **Found two real F#/Fun.Blazor CE parsing traps** while building this:
      a bare `(if cond then "a" else "b")` used directly as a computation-expression statement is
      "ambiguous as part of a computation expression" (fixed by lifting it to a `let` binding
      first); and naming a `let!`-bound value `selected` inside the same `adapt { }` block that
      also contains a `MudTable''{ }` call produced `FS3095: 'selected' is not used correctly.
      This is a custom operation` — renamed to `selectedAccountId` to avoid whatever name Fun.Blazor's
      generated MudTable builder uses internally.
- [x] **8.4** `Pages/Settings/MailAccountsPage.fs`, `routeCi "/settings/mail-accounts"`, registered in
      `Shell.fs` and in `SettingsComponents.settingsNavMenu`.
      *Depends on:* 8.3, 8.2.
- [x] **8.5** The "no profile folder chosen yet" state — an explicit invitation, **not** an empty
      table. The app is unusable until this is set, and the page must say so.
- [x] **8.6** Per-account "count messages" action showing the cached count and **when it was taken**.
      *Depends on:* 8.3, 4.5.
- [x] **8.7** "Full rescan" action that clears the account's watermarks.
      *Depends on:* 8.3, 4.4.

## Phase 9 — Contract suites (required)

- [x] **9.1** One shared suite per dependency function type — all eleven, **including
      `ReadMailFolder`, which change #4 consumes** — run against the real adapter and every fake.
      `MemberData` sources are **public** `let`s.
      *Note:* grouped into seven suites (Load/Save pairs share one, matching
      `SupplierDependencyContractTests`'s precedent of grouping related CRUD functions) plus
      `FolderPicker`'s own — ten domain types + `FolderPicker` = eleven.
      `FolderPicker` has no "real" implementation to run headlessly (it opens a native dialog);
      its suite runs both fake outcomes (`Some path`, `None`) instead, stated plainly rather than
      silently only covering the domain types.
- [x] **9.2** `MailAccountApi` contract suite: real record and every fake.
- [x] **9.3** Persisted-shape tests for all five LiteDB entities — assert the stored document's
      **field names**, not just the round-tripped object.
      *Note:* this phase's tests reach the known intermittent LiteDB `BsonMapper` warm-up race
      (CLAUDE-project.md → *Per-integration databases*) more often than earlier phases simply
      because there are now more parallel test classes constructing a `ThunderbirdDatabaseContext`
      at once — seen as `System.InvalidOperationException: Collection was modified` from
      `BsonMapper.SerializeObject` inside `ThunderbirdDatabaseContextModule.getDatabaseContext`'s
      warm-up line, in `MailAccountApiContractTests` on one run and
      `ThunderbirdDependencyContractTests` on the next. This is the pre-existing, documented,
      accepted race (not a new bug this change introduced), and per CLAUDE-project.md it is out
      of scope to fix here — closing it needs a process-wide warm-up lock and its own change
      folder. Re-running the full suite reliably clears it (confirmed clean across the runs
      recorded for the Phase 11 gate).

## Phase 10 — End to end (required)

- [x] **10.1** `E2E/MailAccountsFlowTests.fs` with a lambda `FolderPicker`, against the Phase 1
      fixtures and a real temp LiteDB: choose a folder → the path shows; scan → accounts appear;
      select → shown selected and persists across a reload; a walk hitting an unreadable directory →
      the directory and reason are listed **and the accounts still appear**; a failure → `MudAlert`,
      cleared by the next success; no folder chosen → the invitation, not an empty table.
      *Note:* the unreadable-directory test reuses Phase 3's `icacls`-deny approach at test setup
      (a synthetic profile + a permission-denied sibling directory), matching
      `Fixtures/ThunderbirdWalk/README.md`'s guidance to construct this at test time rather than
      commit it. All 5 tests passed on the first run, including the one that drives the real
      rendered "Browse" button through to the injected `FolderPicker` lambda - proving Phase 7's
      host wiring end to end without a real window.
- [x] **10.2** Confirm no test opens a window and no test reaches `Startup.Startup`.
      *Outcome:* `grep` across `MyDogsbody.Tests/` for `Startup.Startup`, `OpenFolderDialog` and
      `ShowDialog` finds only doc-comment references stating that it is avoided, and generated
      XML-doc artifacts - no test file reaches either.

## Phase 11 — Gate (required)

- [x] **11.1** `dotnet build MyDogsbody.sln` — zero errors.
- [x] **11.2** `dotnet test` — zero failures, **zero skips**, all four levels. Record totals per level.
      *Totals:* 561 Unit + 222 Integration + 244 Contract + 27 E2E = **1054**, zero failures, zero
      skips (full-suite run confirmed clean after the known LiteDB `BsonMapper` warm-up race —
      see Phase 9.3's note — was hit and cleared on retry; re-measured during PR #17's round-1
      review-fix, which found the count first recorded here undercounted the committed suite, and
      raised again by PR #17's round-2 review-fix (+21), round-3 (+16), round-4 (+2), round-5
      (+12), round-6 (+3), round-7 (+2), round-8 (+10) and round-9 (+4) — see `outcome.md`'s *Gate* section,
      which carries the per-round table. Round 4's finding was that this line had been left at
      round 2's figure while the same figure was corrected in `outcome.md` and
      `CLAUDE-project.md`, and round 8 found the same three-way drift again; keep all three in
      step).
- [x] **11.3** `Contracts/DomainIsolationTests.fs` and `AssertDomainReferencesNothing` still pass.
- [x] **11.4** **Manual verification against the real profile, with Thunderbird running.** Record in
      `outcome.md`:
      the account count found (**expected: the number `prefs.js` declares, not the directory count**);
      how long full folder enumeration took;
      how long a headers-only count took for the largest account;
      that the profile was unmodified and Thunderbird noticed nothing.
      **The third number is what friction #14 and Q1.9 ride on — change #4 needs it before
      rescan-on-every-click can be treated as settled.**
      *Done 2026-08-22*, against `C:\Users\jygcn\AppData\Roaming\Thunderbird\Profiles\49stkd1y.default`
      with Thunderbird running throughout (6 processes, its usual multi-process shape), via a
      throwaway console harness referencing `MyDogsbody.Startup` per CLAUDE-project.md's guidance
      (kept outside the repo, deleted afterwards along with the `.db` files it produced — those
      held a real snapshot of account discovery data). Numbers recorded in `outcome.md`; account
      email addresses are deliberately not reproduced there, matching background.md's own
      "no invoice contents, amounts, references or addresses in version control" policy for this
      series.

## Phase 12 — Documentation (required)

- [x] **12.1** `CLAUDE-project.md`: two new projects in the structure table, the new domain area, the
      MimeKit dependency, the *Build state* totals, and a note that the WPF host now supplies a
      folder picker.
- [x] **12.2** `outcome.md`: totals per level, the manual numbers from 11.4, and — **stated plainly**
      — that maildir ships verified against **synthetic fixtures only**, because the measured profile
      contains none.
- [ ] **12.3** Open `change/thunderbird-account-selection` for review, with this file's checkboxes
      ticked and `outcome.md` on the branch. **Merge only after Phase 11 passed in full.**
      *Point the reviewer at Phase 7 — this is the only change in the series that touches the WPF
      host, and the diff there should be one function and one registration.*
      *Status:* everything on the branch is complete and ungated-green locally; committing,
      pushing and opening the PR are left for explicit confirmation rather than done
      autonomously — see the summary at the end of this session.

---

## Optional

- [ ] **O.1** Read `global-messages-db.sqlite` (gloda) as a fast path for message counts and date
      filtering, falling back to the mbox walk. It is a real SQLite index, but it is not guaranteed
      enabled or current, so it can only ever be an optimisation over a correct slow path.
- [ ] **O.2** Show per-folder sizes in the accounts table, so the cost of a scan is visible before it
      is run. The data is already collected.
- [ ] **O.3** Let the user override the folder exclusions. The measured defaults remove 9.0 of
      15.2 GB, so the default is right; someone who files invoices in a folder called `Archive-Sent`
      would want this.
- [ ] **O.4** Detect and report a profile that is currently being written by a *different*
      Thunderbird version.

## Known risks carried into this change

- **Friction #14 — 16 GB.** Every performance assumption in this change is checked by task 11.4
  against the real store, not against a fixture.
- **Friction #3 — reading a live store.** `FileShare.ReadWrite`, torn final message discarded, locked
  folder reported and skipped. Task 4.1 asserts the fixture files are **byte-identical after a read**.
- **Friction #13 — the walk.** Depth bound, junction loop guard, per-directory errors, and "no
  accounts here" distinguished from "I could not look".
- **Friction #4 — `.msf` is Mork.** Ignored entirely; task 3.3 asserts an `.msf` with no matching
  mbox invents no folder.
- **Friction #12 — the first host change.** Task 7.3 exists to keep it to two things.
- **Maildir is unverified against real data** and the change description must say so.
- **The LiteDB global mapper race** — warm-up for all five entities, task 5.1.
