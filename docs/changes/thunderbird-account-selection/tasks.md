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

- [ ] **0.1** Create `MyDogsbody.Integrations.Thunderbird.Database.Models` (C#, net9.0). Empty for now.
- [ ] **0.2** Create `MyDogsbody.Integrations.Thunderbird` (F#, net9.0), referencing `MyDogsbody.Domain`,
      `MyDogsbody.Builders`, `MyDogsbody.Exceptions.Types` and 0.1. Add the **MimeKit** package here
      and nowhere else.
- [ ] **0.3** Add both to `MyDogsbody.sln`, and a `ProjectReference` from `MyDogsbody.Tests`.
      *Outcome:* solution builds; `MyDogsbody.Domain` still has zero `ProjectReference` elements.

## Phase 1 — Fixtures (required, first)

- [ ] **1.1** `Fixtures/ThunderbirdProfiles/measured-shape/` — a synthetic profile reproducing every
      trap from the measured one: 10 accounts in `mail.accountmanager.accounts` with `lastKey = 20`
      and gaps at 4, 5, 7, 8, 11–16; 15 directories under `ImapMail/` of which 6 have no account;
      numeric-infix directory names; a stale absolute `directory` beside a correct `directory-rel`;
      one account whose store directory is absent; one account with two identities; `.sbd` nesting
      three deep; `.msf` files with no matching mbox; `Trash`/`Junk`/`Sent`/`Drafts` present and sized.
      *Outcome:* committed, small, containing no real mail.
- [ ] **1.2** `Fixtures/ThunderbirdProfiles/maildir-shape/` — a synthetic maildir account
      (`cur`/`new`/`tmp`). **Synthetic only** (Q4.11); state that limitation in `outcome.md`.
- [ ] **1.3** `Fixtures/Mbox/` reader fixtures: a `From `-quoted body line; a final message truncated
      mid-headers; a message with no `Message-ID`; a message with an unparseable `Date`; a message
      with both `text/plain` and `text/html`; a PDF attachment declared `application/octet-stream`;
      an attachment with no filename; **and a fixture whose pre-cutoff messages carry deliberately
      malformed MIME**, so a test can prove they were skipped without being parsed.
- [ ] **1.4** `Fixtures/` for the walk: an unreadable directory, a directory junction pointing at an
      ancestor, and a tree deeper than the depth bound.
      *Note:* build the junction at test setup rather than committing it — Git cannot carry one.

## Phase 2 — Domain (required)

- [ ] **2.1** *(test-first)* `ProfileRootPath`, `MailAccountId`, `ScanCutoff` in
      `Domain/MailAccounts/MailAccountsTypes.fs`.
      Tests: one accepted and one rejected value per rule with the reason asserted;
      **`ScanCutoff.ofStartOfDay` truncates the time** — the same instant at 09:00 and 17:00 gives
      one cutoff (Q1.18).
- [ ] **2.2** `StoreFormat`, `MailFolder`, `DiscoveredMailAccount`, `UnreadableDirectory`,
      `DiscoveryResult`, `MailAttachment`, `MailMessage`, `MailAccountError`, and the eleven
      dependency function types.
      *Depends on:* 2.1.
- [ ] **2.3** *(test-first)* `SetProfileRootWorkflow`.
      Tests: Ok path; `ProfileRootInvalid` on an empty or relative path with the reason;
      `saveProfileRoot` never called on a validation failure.
- [ ] **2.4** *(test-first)* `ScanForMailAccountsWorkflow`.
      Tests: Ok path with every field of the result asserted; `ProfileRootMissing` when none is set,
      **and `discoverMailAccounts` never called**; unreadable directories are carried into the
      result rather than failing it; **selection reconciliation** — a stored selection naming an
      account absent from the fresh discovery is cleared and reported, and one still present is
      left alone.
      *Depends on:* 2.2.
- [ ] **2.5** *(test-first)* `ListMailAccountsWorkflow`. Tests: accounts and selection returned
      together; empty store returns an empty list and `None`, not an error.
- [ ] **2.6** *(test-first)* `SelectMailAccountWorkflow`. Tests: Ok path; `MailAccountNotFound` for
      an id not among the stored accounts, **with `saveSelectedMailAccount` never called**.

## Phase 3 — Discovery adapters (required)

- [ ] **3.1** *(test-first)* `ThunderbirdFolderScanner.fs` — recursive `prefs.js` walk.
      Tests *(Integration, against Phase 1 fixtures)*: the chosen folder being one profile, the
      parent of several, and a backup copy; an unreadable directory is **recorded and the walk
      continues**; a junction pointing at an ancestor does not loop; the depth bound stops the walk;
      a folder with no `prefs.js` yields `NoProfileFound`, not an empty account list.
      *Depends on:* 1.1, 1.4, 2.2.
- [ ] **3.2** *(test-first)* `ThunderbirdAccountReader.fs` — `prefs.js` → accounts.
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
- [ ] **3.3** *(test-first)* `MailFolderEnumerator.fs` — mbox and maildir.
      Tests: an extensionless file is a folder and its sibling `.sbd` holds its children, three
      levels deep; a maildir directory holding `cur`/`new`/`tmp` is a folder; **`.msf` files are
      ignored entirely**, including one with no matching mbox; `Trash`, `Deleted`, `Junk`, `Sent`
      and `Drafts` come back with `IsScannable = false`; sizes are reported; a zero-byte file is an
      empty folder, not an error; a folder whose mbox and `.sbd` differ in case are still paired.
      *Depends on:* 3.2.

## Phase 4 — The reader (required) — the part that touches a live 16 GB store

- [ ] **4.1** *(test-first)* `MailFolderReader.fs` — opening and message boundaries.
      Tests: opened with `FileAccess.Read` and `FileShare.ReadWrite`; **the fixture files are
      byte-identical after a read** (read-only by construction, asserted rather than assumed);
      a `From `-quoted body line does not start a new message; a truncated final message is
      discarded and everything before it returned; a locked file yields `MailFolderUnreadable`
      **and the other folders still return**.
      *Depends on:* 3.3, 1.3.
- [ ] **4.2** *(test-first)* The cutoff.
      Tests: a message older than the cutoff is skipped; **it is skipped without its body being
      parsed** — proved by the fixture whose pre-cutoff messages carry malformed MIME that would
      throw; a message with a missing or unparseable `Date` is **included**; a message exactly on
      the cutoff boundary is included.
      *Depends on:* 4.1.
- [ ] **4.3** *(test-first)* Message content.
      Tests: `Message-ID` used when present; **a synthesised id is stable across a compaction** —
      read, rewrite the fixture with a message removed from the front, read again, the surviving
      message keeps its id; attachments returned as **bytes** with no temp file created anywhere
      (asserted by watching the temp directory); both body alternatives returned when present;
      `DeclaredContentType` carried even when it disagrees with the extension; an attachment with no
      filename gets a generated name stating its content type.
      *Depends on:* 4.2.
- [ ] **4.4** *(test-first)* Watermarks and incremental reads.
      Tests: a first read records size, mtime and offset; a second read with the file unchanged
      returns nothing new; appending a message makes the second read return **only** that message;
      a shrunk file discards the watermark and re-reads the whole folder; an inconsistent mtime does
      the same; `ClearWatermarks` forces a full re-read.
      *Depends on:* 4.3, 5.1.
- [ ] **4.5** *(test-first)* `countMessages` — a headers-only pass.
      Tests: the count matches the fixture; no body or attachment is parsed.
      *Depends on:* 4.1.

## Phase 5 — Integration store (required)

- [ ] **5.1** *(test-first)* Five C# entities in `.Database.Models`, plus
      `Database/ThunderbirdDatabaseContextModule.fs` with five getters and a `Dispose`.
      Tests *(Integration)*: a context over a temp database (`connection=direct`) can be disposed and
      **the file then deletes successfully**; a `BsonMapper.Global.ToDocument` warm-up runs for
      **every one of the five** entities before the context is returned.
      *Depends on:* 0.1, 0.2.
- [ ] **5.2** *(test-first)* `ThunderbirdEntityMappers.fs` — the bottom mapper.
      Tests: field-for-field both directions for all five entities; `StoreFormat` ⇄ its persisted
      string; an unrecognised stored value maps to an error rather than a default.
      *Depends on:* 5.1, 2.2.
- [ ] **5.3** *(test-first)* `ThunderbirdStore.fs` — profile root, accounts, folders, selection,
      watermarks. Outer-ring shape: `handleError` first, `Result<_, MyDogsbodyException>` out.
      Tests *(Integration)*: round trips for each; saving accounts **replaces** the previous set
      rather than accumulating; clearing the selection persists as absent, not as an empty string.
      Tests *(Unit)*: each error path asserts its declared `ActionNames` string, its message and a
      preserved inner exception.
      *Depends on:* 5.2.
- [ ] **5.4** `ActionNames.MyDogsbody.Integrations.Thunderbird.*` — one entry per adapter function.
      *Outcome:* the structural suite still passes.

## Phase 6 — Composition root (required)

- [ ] **6.1** *(test-first)* `MailAccountApiMappers.fs` — domain ⇄ UI, `toMailAccountError`,
      `toMyDogsbodyException`.
      Tests: field-for-field both directions; each `MailAccountError` case → its intended action and
      message, with the **expected/unexpected split** asserted — in particular that
      `MailFolderUnreadable` and `ProfileUnreadable` are **expected** and therefore **not logged**
      (design → *Error handling*).
- [ ] **6.2** *(test-first)* `MailAccountApiFactory.createMailAccountApi handleError thunderbirdContext`.
      Tests *(Integration)*: each API member against a real temp LiteDB and the Phase 1 fixtures.
      No module-level I/O.
      *Depends on:* 5.3, 6.1, 4.5.
- [ ] **6.3** `ActionNames.MyDogsbody.Startup.MailAccountApi.*`.
- [ ] **6.4** `Startup.fs`: `Thunderbird.db` context, `mailAccountApi`, one more registration.

## Phase 7 — The host (required) — friction #12

- [ ] **7.1** `FolderPicker = unit -> string option` in `MyDogsbody.UI.Types`.
- [ ] **7.2** In the WPF host, implement it with `Microsoft.Win32.OpenFolderDialog` and register it.
      *Outcome:* **this is the only change to the host in the whole series, and it should be one
      function plus one registration.** Nothing else in `MainWindow.xaml.cs` moves.
      *Depends on:* 7.1.
- [ ] **7.3** Confirm the diff of the host project is exactly those two things.

## Phase 8 — UI (required)

- [ ] **8.1** `MyDogsbody.UI.Types`: `MailAccountUiType`, `MailFolderUiType`,
      `DiscoveryResultUiType`, `MailAccountApi`, `Modules/MailAccountsBrowserModule.fs`.
- [ ] **8.2** *(test-first)* `ModuleCreators/MailAccountsBrowserModuleCreators.fs` — `cval`/`transact`,
      `startWork` first, write-then-reload.
      Tests: choosing a folder then scanning reloads the table; selecting an account persists and
      reloads; a failure sets `ErrorAval` and a later success clears it; a scan in progress is
      visible; **no `Async.Start` in the file**.
- [ ] **8.3** `Components/MailAccountsComponents.fs` — the accounts table with the folder-path row,
      Browse button, scan action, unreadable-directory list, and the per-row selection radio.
      *Outcome:* an account marked configured-but-missing is shown **in** the table, marked.
- [ ] **8.4** `Pages/Settings/MailAccountsPage.fs`, `routeCi "/settings/mail-accounts"`, registered in
      `Shell.fs` and in `SettingsComponents.settingsNavMenu`.
      *Depends on:* 8.3, 8.2.
- [ ] **8.5** The "no profile folder chosen yet" state — an explicit invitation, **not** an empty
      table. The app is unusable until this is set, and the page must say so.
- [ ] **8.6** Per-account "count messages" action showing the cached count and **when it was taken**.
      *Depends on:* 8.3, 4.5.
- [ ] **8.7** "Full rescan" action that clears the account's watermarks.
      *Depends on:* 8.3, 4.4.

## Phase 9 — Contract suites (required)

- [ ] **9.1** One shared suite per dependency function type — all eleven, **including
      `ReadMailFolder`, which change #4 consumes** — run against the real adapter and every fake.
      `MemberData` sources are **public** `let`s.
- [ ] **9.2** `MailAccountApi` contract suite: real record and every fake.
- [ ] **9.3** Persisted-shape tests for all five LiteDB entities — assert the stored document's
      **field names**, not just the round-tripped object.

## Phase 10 — End to end (required)

- [ ] **10.1** `E2E/MailAccountsFlowTests.fs` with a lambda `FolderPicker`, against the Phase 1
      fixtures and a real temp LiteDB: choose a folder → the path shows; scan → accounts appear;
      select → shown selected and persists across a reload; a walk hitting an unreadable directory →
      the directory and reason are listed **and the accounts still appear**; a failure → `MudAlert`,
      cleared by the next success; no folder chosen → the invitation, not an empty table.
- [ ] **10.2** Confirm no test opens a window and no test reaches `Startup.Startup`.

## Phase 11 — Gate (required)

- [ ] **11.1** `dotnet build MyDogsbody.sln` — zero errors.
- [ ] **11.2** `dotnet test` — zero failures, **zero skips**, all four levels. Record totals per level.
- [ ] **11.3** `Contracts/DomainIsolationTests.fs` and `AssertDomainReferencesNothing` still pass.
- [ ] **11.4** **Manual verification against the real profile, with Thunderbird running.** Record in
      `outcome.md`:
      the account count found (**expected: the number `prefs.js` declares, not the directory count**);
      how long full folder enumeration took;
      how long a headers-only count took for the largest account;
      that the profile was unmodified and Thunderbird noticed nothing.
      **The third number is what friction #14 and Q1.9 ride on — change #4 needs it before
      rescan-on-every-click can be treated as settled.**

## Phase 12 — Documentation (required)

- [ ] **12.1** `CLAUDE-project.md`: two new projects in the structure table, the new domain area, the
      MimeKit dependency, the *Build state* totals, and a note that the WPF host now supplies a
      folder picker.
- [ ] **12.2** `outcome.md`: totals per level, the manual numbers from 11.4, and — **stated plainly**
      — that maildir ships verified against **synthetic fixtures only**, because the measured profile
      contains none.
- [ ] **12.3** Open `change/thunderbird-account-selection` for review, with this file's checkboxes
      ticked and `outcome.md` on the branch. **Merge only after Phase 11 passed in full.**
      *Point the reviewer at Phase 7 — this is the only change in the series that touches the WPF
      host, and the diff there should be one function and one registration.*

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
