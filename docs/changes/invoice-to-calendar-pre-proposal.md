# Pre-proposal — Invoices from Thunderbird to Google Calendar

**Status:** pre-proposal. Not a change folder yet — no `requirements.md` exists, and none should be written until the blocking questions in §7 are answered.
**Date:** 2026-08-08

---

## 1. What was asked for

Five user-facing pieces:

1. **An invoice page** with a table that lists every invoice found in one account of the Thunderbird profile folder, and a button that uploads them to a chosen Google account's calendar.
2. **A scan window on that page** — a chooser for how far back to scan the invoice source: **1, 2, 3, 4, 6 or 12 months from today**. The table shows what falls inside the chosen window, and changing it rescans.
3. **A diff in that same table** — for the selected calendar, show which invoices are already on it and which are not.
4. **A Google accounts page** for registering Google calendar integrations. Many Google accounts can be registered at once.
5. **A Thunderbird accounts page** that lists the accounts available in the profile folder and lets one be selected for import.

Everything below is a proposed shape for that, plus the decisions that have to be made before it can be specified. Nothing here is agreed.

---

## 2. What already exists to build on

| Piece | Where | How it helps |
| --- | --- | --- |
| Credential store (LiteDB) with `InfrastructureType.Google` | `MyDogsbody.Integrations.Credentials` | A place a Google client secret / token could live — **but see Q3.3, it may be the wrong home** |
| Credentials page + browser module + editor dialog | `UI.Portal/Pages/Settings/CredentialsPage.fs`, `Components/CredentialsComponents.fs` | The exact template for both new settings pages: table + toolbar button + `FunComponent` dialog |
| `CredentialsBrowserModuleCreators` | `UI.Portal/ModuleCreators/` | The template for adaptive state: `cval` + `transact`, `startWork` as first parameter, write-then-reload, `ErrorAval` |
| PDF text extraction behind a domain dependency type | `MyDogsbody.Domain/Documents/`, `Integrations.Pdf/PdfDocumentReader.fs` | If invoices are PDF attachments, `ReadDocumentContent` already exists and already has a contract suite |
| Google Calendar CRUD prototype | `GoogleCalendarCRUD/Program.fs` (scratch) | Working list/insert/update/delete against `CalendarService`, incl. the `GoogleWebAuthorizationBroker` + `FileDataStore` auth dance |
| `MyDogsbody.Integrations.Google` | one-line stub | The project to grow into the calendar adapter |
| Composition-root split | `Startup/CredentialApiMappers.fs`, `CredentialApiFactory.fs`, `Startup.fs` | The three-file shape each new API record must copy so it stays testable |
| Main SQLite database + FluentMigrator | `MyDogsbody.Database`, `.Database.Migrations` | Designed, migrated, **not wired into the app**. This feature is a candidate to be its first consumer — see Q5.1 |

Two prototypes are *not* useful here: `TestMsGraphToEmails` is empty, and `GNUCashAccess` reads GnuCash XML (its `invoice` namespace is unrelated to email invoices).

There is **nothing** in the repo today that reads Thunderbird — no profile discovery, no mbox/maildir reader, no MIME parser. That is the largest unknown in this proposal.

---

## 3. Proposed shape

### 3.1 Flow

```
Thunderbird profile ──┐
  profiles.ini        │  Integrations.Thunderbird (outer ring)
  prefs.js accounts   ├──►  ListMailAccounts ───────────────────┐
  mbox / maildir      ├──►  ReadMailFolder account cutoff ──────┤
  MIME attachments    ┘                                         ▼
  ScanWindow (from the page) ┐                  Domain/Invoices  ExtractInvoicesWorkflow
  GetCurrentTime (Startup) ──┴──────────────────────────────►      │  cutoff = now - window
                                                                   │  ValidInvoice list
                                                                   ▼
Google Calendar ──────┐                       Domain/Calendar  DiffInvoicesWorkflow  (pure)
  Events.list         ├──►  ListCalendarEvents ────────────────►│
  Events.insert       ├──►  CreateCalendarEvent ◄───────────────┤  UploadInvoicesWorkflow
  CalendarList.list   ├──►  ListCalendars                       │
  OAuth token store   ┘  Integrations.Google (outer ring)       ▼
                                                        InvoiceSyncApi (UI.Types)
                                                                 │
                                                          UI.Portal /invoices
```

### 3.2 Domain (`MyDogsbody.Domain`, references nothing — unchanged)

Two new workflow areas, each one folder, `<Area>Types.fs` first:

**`Invoices/InvoicesTypes.fs`**
- Constrained primitives: `InvoiceReference`, `SupplierName`, `Money` (amount + currency), `InvoiceDate` / `DueDate`, `SourceMessageId`.
- **The scan window is a closed union, not a number** — six choices were asked for, so six cases, and "7 months" cannot be written down:

  ```fsharp
  type ScanWindow =
      | OneMonth | TwoMonths | ThreeMonths | FourMonths | SixMonths | TwelveMonths

  module ScanWindow =
      let months = function
          | OneMonth -> 1 | TwoMonths -> 2 | ThreeMonths -> 3
          | FourMonths -> 4 | SixMonths -> 6 | TwelveMonths -> 12

      /// All six, in display order — the picker renders this rather than its own list, so
      /// adding a window is one edit and the UI cannot drift out of step.
      let all = [ OneMonth; TwoMonths; ThreeMonths; FourMonths; SixMonths; TwelveMonths ]
  ```

  No `create`/`value` pair here: a union of six cases is already impossible to get wrong, so the constrained-primitive shape would be ceremony. Parsing whatever the UI sends back into a `ScanWindow` is the top mapper's job (see §3.5).
- `ScanCutoff` — the instant computed from the window, `private ScanCutoff of DateTime`. Distinct from the window on purpose: a window is a *choice*, a cutoff is a *fact derived from the clock*, and the adapter must be handed the second so it cannot re-derive it differently.
- Stage types: `UnvalidatedInvoice` (raw strings pulled out of a message) → `ValidInvoice` → `SyncedInvoice` (carries the calendar event id it produced).
- Error DU: `InvoiceError` — `InvoiceReferenceInvalid`, `AmountUnparseable`, `DueDateMissing`, `MailStoreUnreadable of message`, `NoAccountSelected`, …
- Dependency function types: `ListMailAccounts`, `LoadSelectedMailAccount`, `SaveSelectedMailAccount`, and
  - `ReadMailFolder = MailAccountId -> ScanCutoff -> Result<MailMessage list, InvoiceError>` — the cutoff is a parameter so the adapter stops reading rather than reading everything and discarding. On a 12-month window over a large mbox that difference is the whole responsiveness of the page.
  - **`GetCurrentTime = unit -> DateTime`** — new, and required by the rules: CLAUDE.md forbids the domain reading a clock, and "months from present" needs one. Production binds `fun () -> DateTime.Now` at the composition root; every test binds a fixed instant, which is what makes the cutoff arithmetic assertable at all.

  If PDF parsing is in scope, the existing `ReadDocumentContent` is reused rather than redeclared.

**`Calendar/CalendarTypes.fs`**
- `GoogleAccountId`, `CalendarId`, `CalendarEventId`, `CalendarEvent`, `InvoiceSyncKey` (the idempotency key — see Q2.4).
- Error DU: `CalendarError` — `CalendarUnreachable`, `NotAuthorised`, `AccountNotRegistered`, `EventRejected`, …
- Dependency function types: `ListGoogleAccounts`, `RegisterGoogleAccount`, `ListCalendars`, `ListCalendarEvents`, `CreateCalendarEvent`.

**Workflows** (one file each, one public function each):
- `ExtractInvoicesWorkflow` — `getCurrentTime → readMailFolder → (account, ScanWindow) → ValidInvoice list`. The cutoff arithmetic (`now.AddMonths(-months)`) is a private pure function in this file, so "6 months back from 31 March" is a unit test with a fixed clock and no mail store anywhere near it.
- `DiffInvoicesAgainstCalendarWorkflow` — **pure, no dependencies**: `ValidInvoice list -> CalendarEvent list -> InvoiceSyncStatus list`. This is the heart of the feature and the cheapest thing in it to test, provided the match key is a value and not a heuristic.
- `UploadInvoicesToCalendarWorkflow` — takes the diff result, calls `CreateCalendarEvent` for the missing ones only.
- `ListMailAccountsWorkflow`, `SelectMailAccountWorkflow`, `RegisterGoogleAccountWorkflow`, `ListGoogleAccountsWorkflow`.

### 3.3 Outer ring

**`MyDogsbody.Integrations.Thunderbird`** (new) + `.Database.Models` (C#, if it stores anything)
- `ThunderbirdProfileReader.fs` — `profiles.ini` → profile paths.
- `ThunderbirdAccountReader.fs` — `prefs.js` → account list (`mail.account.*`, `mail.server.serverN.*`).
- `MailFolderReader.fs` — mbox and/or maildir → messages; MIME parsing (MimeKit is the realistic pick) for attachments. **Takes the cutoff and honours it while reading**: skip a message on its `Date`/received header before touching its body or attachments, so a 1-month window doesn't pay for a 12-month mbox. mbox is append-ordered in practice but not guaranteed sorted, so it still has to be walked end to end — the saving is in not parsing MIME or opening PDFs for messages outside the window, which is where the time actually goes.
- Its own store for the selected-account setting, unless that goes to the main database (Q5.1).

**`MyDogsbody.Integrations.Google`** (exists as a stub)
- `GoogleCalendarClient.fs` — `CalendarService` behind `ListCalendars` / `ListCalendarEvents` / `CreateCalendarEvent`, lifted from the `GoogleCalendarCRUD` prototype.
- `GoogleAccountStore.fs` — registered accounts + their OAuth tokens (Q3.3 decides where).
- `GoogleAuthorization.fs` — the consent flow (Q3.1).

Both follow the existing rules: `Result<'T, MyDogsbodyException>`, `handleError`, an `ActionNames` entry per function, the collection getter stopping at the integration boundary, and a `BsonMapper.Global.ToDocument` warm-up in any new LiteDB context.

### 3.4 Composition root

Three new API factories, each in the established three-file split with no module-level I/O outside `Startup.fs`:

- `GoogleAccountApiFactory.fs` + `GoogleAccountApiMappers.fs`
- `MailAccountApiFactory.fs` + `MailAccountApiMappers.fs`
- `InvoiceSyncApiFactory.fs` + `InvoiceSyncApiMappers.fs`

`Startup.fs` gains the new database handles and three lines in `registerServices`. `MainWindow.xaml.cs` does not change.

### 3.5 UI

**`MyDogsbody.UI.Types`** — new records (no domain types leak here):
- `GoogleAccountUiType`, `CalendarUiType`, `MailAccountUiType`, `InvoiceUiType` (with a `SyncStatus` field), and the three API records `GoogleAccountApi`, `MailAccountApi`, `InvoiceSyncApi`.
- **`ScanWindowUiType`** — the UI needs its own, because `UI.Types` cannot reference `MyDogsbody.Domain`. Either a six-case union mirroring the domain's, or a plain `int` of months. Recommend the **union**, mapped in `InvoiceSyncApiMappers` with an exhaustive match in both directions: an `int` puts `7` back on the table and moves the "is this a legal window?" check from the compiler into a runtime error. This is the same tax `Infrastructure` ⇄ `InfrastructureType` already pays, for the same reason.
- `Modules/GoogleAccountsBrowserModule.fs`, `Modules/MailAccountsBrowserModule.fs`, `Modules/InvoiceSyncModule.fs` — each a record of `aval` fields + commands, same shape as `CredentialsBrowserModule`. `InvoiceSyncModule` carries `SelectedScanWindowAval: aval<ScanWindowUiType>`, `AvailableScanWindowsAval` and `SelectScanWindow: ScanWindowUiType -> unit`; selecting one is a write-then-reload exactly like an edit, so the table always shows what was actually scanned rather than what the picker was holding.

**`MyDogsbody.UI.Portal`** — three pages, registered in `Shell.fs`, the two settings pages also linked from `SettingsComponents.settingsNavMenu`:

| Page | Route | Contents |
| --- | --- | --- |
| Invoices | `/invoices` (top-level, not settings) | **Scan-window picker (1 / 2 / 3 / 4 / 6 / 12 months)**, Google-account + calendar pickers, refresh, invoice table with a per-row sync-status column, "Upload to calendar" button, `MudAlert` from `ErrorAval` |
| Google accounts | `/settings/google-accounts` | Table of registered accounts, "Add account" → consent flow, remove/re-authorise |
| Thunderbird accounts | `/settings/mail-accounts` | Table of accounts discovered in the profile, radio/select for the one to import from |

The scan-window picker is six fixed choices, so a `MudToggleGroup` of six buttons reads better than a dropdown — the whole range is visible and switching is one click, which matters if the intended use is "try 3, then look further back". A `MudSelect` is the fallback if the toolbar gets crowded. Either way it renders `AvailableScanWindowsAval`, never a hard-coded list in the component.

---

## 4. Proposed change breakdown

One change folder for all of this would be too large to review or to test in the CLAUDE.md sense. Suggested sequence — each is independently shippable and each gets its own `docs/changes/<name>/`:

| # | Change | Delivers | Depends on |
| --- | --- | --- | --- |
| 1 | `google-account-integration` | Google accounts page, OAuth registration, calendar listing. Ask #4. | — |
| 2 | `thunderbird-account-selection` | Thunderbird accounts page, profile/account discovery, selection persisted. Ask #5. | — |
| 3 | `invoice-extraction` | Invoices read out of the selected account into `ValidInvoice list`, **with the scan-window picker driving the read** — the window belongs here, not in #4, because it is what the scan is bounded by. Read-only table, no calendar involvement. Ask #2. | 2 |
| 4 | `invoice-calendar-sync` | The diff, the sync-status column, the upload button. Asks #1 and #3. | 1, 3 |

1 and 2 are independent and could be done in either order. If you want something visible sooner, 2 → 3 gives a working invoice table with a working scan window and no Google involvement at all — which is also the cheapest way to find out whether the extraction in Q1.1–Q1.4 actually works against your mail before any calendar code exists.

---

## 5. Where this rubs against the current architecture

These are real, and each needs a decision — they are not blockers, but pretending they aren't there will cost a rewrite.

1. **The Google client is async; domain workflows are synchronous `Result`.** The prototype uses `.Result`, which blocks. Options: keep blocking (calls already run off the render thread via `startWork`), or take the **FsToolkit.ErrorHandling** dependency for `asyncResult`, which CLAUDE-project.md already contemplates for exactly this case. Recommendation: blocking for change #1, revisit if the upload batch makes the UI feel stuck.
2. **Contract tests against a network service.** CLAUDE.md requires each dependency function type's shared suite to run against the real adapter *and* every fake. For `CreateCalendarEvent` the real adapter is Google. Realistic answer: run the suite against the fakes plus a recorded/stubbed `HttpMessageHandler`, and record live verification as manual coverage in the change description. This must be stated explicitly rather than quietly skipped.
3. **Thunderbird files may be locked or mid-write** while Thunderbird itself is running. Reading mbox under a live lock is a genuine failure mode, not an edge case — it needs a named `InvoiceError` case and a sentence on screen.
4. **`.msf` index files are Mork format** — do not parse them. Either read the mail store directly, or read `global-messages-db.sqlite` (gloda), which is a SQLite index but is not guaranteed to be enabled or current.
5. **Secrets at rest.** The existing credential store persists secrets in plaintext LiteDB. OAuth refresh tokens are materially worse to leak than the current contents. If this feature should encrypt (DPAPI is the low-friction Windows answer), say so now — retrofitting encryption over stored tokens is its own change.
6. **`Startup.fs` opens its databases at module load.** Three more stores means three more files opened in the working directory on first touch. Fine, but tests must keep away from `Startup` exactly as they do today.
7. **The clock is a new dependency function type, and CLAUDE.md calls those published interfaces** — meaning `GetCurrentTime` owes a contract suite run against the real implementation *and* every fake. The real implementation is `DateTime.Now`, whose whole nature is to return something different each call, so "assert both sides agree" has no obvious meaning. Workable answer: the suite asserts the properties that must hold of any clock (monotonic across two calls, `Kind` as expected, within a tolerance of `DateTime.Now` at the point of test) and the *cutoff arithmetic* — the part with actual logic — is unit-tested against fixed instants in the workflow. Say this explicitly in the change rather than quietly having no contract test for the clock. Worth checking whether month arithmetic near month ends behaves as you'd want: `.AddMonths(-1)` from 31 March gives 28 February, not 3 March.

---

## 6. What would be true when it's done

- `dotnet build MyDogsbody.sln` clean; `dotnet test` green with zero skips, across all four levels — with the one honest exception in §5.2 declared in the change description.
- `MyDogsbody.Domain` still has zero `ProjectReference` elements (`AssertDomainReferencesNothing` + `Contracts/DomainIsolationTests.fs` both still pass).
- Still exactly two mapping points per feature: entity ⇄ domain in each integration, domain ⇄ UI record in `Startup/*ApiMappers.fs`.
- `UI.Portal` still references only `UI.Types`, `Enums`, `Exceptions.Types`.
- Uploading twice in a row creates no duplicate events.
- The six scan windows exist in exactly two places — the domain union and its UI mirror — with an exhaustive match between them, so a seventh could never be half-added. No component holds its own list of months.
- No workflow reads the clock directly; every cutoff test pins a fixed instant and asserts an exact date.

---

## 7. Questions to answer

Answer the **blocking** ones before any `requirements.md` is written. The rest can be decided during design, but earlier is cheaper. Each carries my recommendation — "default" is what I'd write if you just said "use your judgement".

### 7.1 What an invoice *is*, and how far back to look — blocking

- **Q1.1 — Where does the invoice data actually live?** A message whose *subject/body* contains the invoice details, a *PDF attachment*, or both? *This single answer decides whether change #3 reuses `ReadDocumentContent` and PdfPig, or is pure text parsing.*
  *Default:* PDF attachment, parsed with the existing PdfPig adapter, with the email supplying date and sender.
- **Q1.2 — Which fields make up an invoice?** Please list them. Candidates: supplier, invoice number, issue date, **due date**, amount, currency, message id, attachment filename.
  *Default:* supplier, invoice number, due date, amount, currency, source message id.
- **Q1.3 — How is an invoice recognised among ordinary mail?** A dedicated Thunderbird folder, a tag, a sender allow-list, a subject pattern, or "every message with a PDF attachment"?
  *Default:* a nominated folder within the account — simplest to explain and to test.
- **Q1.4 — How many suppliers, and are their invoices laid out alike?** One parser or a per-supplier template set? Roughly how many invoices in a typical account — dozens, hundreds, thousands? (Drives paging and whether extraction is cached.)
  *Default:* one parser, extended per supplier as needed; assume hundreds and add a cache in change #3 only if the table feels slow.
- **Q1.5 — What happens to a message the parser can't read?** Skipped silently, listed in the table as "could not read", or a hard error for the whole load?
  *Default:* listed as unparsed with a reason, so nothing disappears without the user seeing it.
- **Q1.6 — Which date does the scan window measure?** This is the one that changes behaviour rather than wording, and there are three candidates:
  - **the date the mail arrived** — sits in the message header, so the reader can skip a message without parsing it, which is what makes a 1-month window fast;
  - **the invoice issue date** — inside the document, so every message in the mbox must be fully parsed before it can be excluded, and the window stops being an optimisation;
  - **the due date** — same cost, and it points *forwards*, so "3 months" would mean something different again.

  They disagree in practice: an invoice that arrived five months ago but falls due next week is inside a 12-month window and outside a 3-month one on the first reading, and the reverse on the third.
  *Default:* the date the mail arrived, with the picker labelled so it says that out loud ("mail received in the last 3 months") rather than leaving you to guess.
- **Q1.7 — Which window is selected on first open, and is the choice remembered** between runs? If remembered, it lands wherever Q5.1 puts the selected mail account.
  *Default:* 3 months, remembered alongside the selected account.
- **Q1.8 — Is 12 months the ceiling?** You named six values topping out at a year — confirming there's no "everything" option keeps the picker honest and stops a first run from walking a decade of mail.
  *Default:* 12 months is the maximum; no all-time option.
- **Q1.9 — Does changing the window rescan immediately, or after a Refresh click?** Immediate is nicer until a 12-month scan over a large mbox takes noticeable seconds, at which point every click through the picker costs one.
  *Default:* immediate, with the table showing the existing loading state. If change #3 shows real scans are slow, add caching then rather than a Refresh button.

### 7.2 What lands on the calendar — blocking

- **Q2.1 — What kind of event?** All-day on the due date, or timed? If timed, what time and duration?
  *Default:* all-day event on the due date.
- **Q2.2 — What does the event say?** Give a title format and whether the description carries the amount/supplier/invoice number. A reminder/notification?
  *Default:* title `Invoice <ref> — <supplier> <amount>`, description carrying all fields, no reminder.
- **Q2.3 — Which calendar?** The account's `primary`, or a calendar the user picks per account?
  *Default:* picked per account, defaulting to `primary`.
- **Q2.4 — What makes the diff "already there"?** This is the crux. Matching on title+date is fragile; the robust answer is stamping each created event with a **private extended property** carrying the invoice reference, then querying `Events.list` with `privateExtendedProperty`. That survives the user renaming or moving the event.
  *Default:* private extended property. Confirm — it's the one decision that's expensive to change later.
- **Q2.5 — Over what time window does the diff look?** `Events.list` needs a bound, and now there are two windows in play — the mail scan window from Q1.6 and this one. Letting them drift apart produces nonsense: query 12 months of calendar against a 1-month scan and every older event reads as "on the calendar but missing from the source".
  *Default:* derive this one from the invoices actually found — the range they span, padded a month either side — so the scan window drives both and they cannot disagree. The diff then only ever answers "of what I scanned, what is already up there?".
- **Q2.6 — Insert-only, or also update and delete?** If an invoice's amount changes after upload, does the existing event get updated? If the user deletes the event by hand, does the next upload put it back?
  *Default:* insert-only for change #4; deletion by hand means it comes back on the next upload. Say so on screen.
- **Q2.7 — Upload everything, or a selection?** Checkboxes per row, or one "upload all missing" button?
  *Default:* one button uploading everything currently missing, with the count shown on it.
- **Q2.8 — Partial failure mid-batch?** Stop at the first failure, or continue and report "14 of 17 uploaded, 3 failed"?
  *Default:* continue, then report per-row outcomes.
- **Q2.9 — What does narrowing the window do to invoices you already uploaded?** They drop out of the table, because the table only shows what was scanned. Nothing is deleted from the calendar — but "it vanished from the list" and "it was removed" look identical on screen if the page doesn't say which happened.
  *Default:* the table shows only the scanned window, with the count and the window stated above it ("41 invoices received in the last 3 months"), so the list is obviously a view of a range rather than a complete record. The upload button acts only on what is visible.

### 7.3 Google accounts — blocking

- **Q3.1 — How does consent happen?** `GoogleWebAuthorizationBroker` (as in the prototype) opens the *system* browser and listens on a loopback port. Acceptable inside this WPF/BlazorWebView app, or should the consent page render inside the WebView?
  *Default:* system browser + loopback. It's what the prototype already does and it's the flow Google recommends for desktop apps.
- **Q3.2 — Where does the OAuth client secret (`credentials.json`) come from?** Shipped with the app, pasted by you once, or one per account?
  *Default:* one client secret for the app, pasted once and stored, reused by every account.
- **Q3.3 — Where do per-account tokens live?** (a) The existing `Credentials.db` under `InfrastructureType.Google`, (b) a new `GoogleAccounts.db` owned by `Integrations.Google`, or (c) Google's `FileDataStore` on disk as the prototype does.
  *Default:* (b) — the per-integration-store rule points that way, and it keeps the existing credentials page from acquiring a second meaning.
- **Q3.4 — How does the existing `/settings/credentials` page relate to the new Google accounts page?** They will both appear to manage "Google credentials". Does the existing page stay as-is, get a note, or lose its Google option?
  *Default:* both stay; the Google accounts page owns OAuth accounts, the credentials page keeps raw API keys. Worth a one-line explanation on each page.
- **Q3.5 — How is an account labelled in the table?** Showing the email address needs the extra `userinfo.email` scope at consent time. Acceptable, or should accounts get a nickname you type?
  *Default:* request `userinfo.email` and show the address — a nickname on top is fine, but a wrong address is confusing.
- **Q3.6 — Removing an account:** delete the stored token only, or also revoke it at Google?
  *Default:* delete locally; mention that revoking happens in the Google account settings.

### 7.4 Thunderbird accounts — blocking

- **Q4.1 — "Accounts" means what exactly?** The mail accounts configured in Thunderbird (one per server/identity), or folders within one account? And is *one* selected at a time, or several?
  *Default:* Thunderbird accounts, exactly one selected at a time.
- **Q4.2 — How is the profile folder located?** Discovered from `%APPDATA%\Thunderbird\profiles.ini`, or a folder path you set in settings?
  *Default:* discover from `profiles.ini`, with an override field for when that's wrong.
- **Q4.3 — Which storage format do your accounts use?** mbox (the default) or maildir? IMAP accounts store cached copies under `ImapMail/`; POP/Local under `Mail/`. If you can say which of yours matter, change #2 can support just those first.
  *Default:* mbox first, maildir behind a follow-up change.
- **Q4.4 — Is Thunderbird likely to be running at the same time?** Determines whether the reader copies the store to a temp file before parsing.
  *Default:* assume yes; copy before parsing.

### 7.5 Storage and process — decide during design

- **Q5.1 — Which storage tier holds the new state** (selected mail account, registered Google accounts, any extraction cache)? A per-integration LiteDB store each, as today — or is this the change that finally wires up the main SQLite database, with FluentMigrator migrations for it?
  *Default:* per-integration LiteDB, matching what exists. Wiring up SQLite is worth doing but shouldn't ride along inside this feature.
- **Q5.2 — Do you accept the contract-test compromise in §5.2** (fakes + stubbed HTTP, with live Google verified manually and recorded as such)?
  *Default:* yes.
- **Q5.3 — Test fixtures:** may sample Thunderbird mbox files and sample invoice PDFs be committed to the repo for tests? They'd need anonymising.
  *Default:* yes, hand-built synthetic ones rather than real mail.
- **Q5.4 — Does the change breakdown in §4 suit you,** or do you want this as one change folder?
  *Default:* four changes in the order given.
- **Q5.5 — Should `GoogleCalendarCRUD` be deleted** once `Integrations.Google` does the same work for real?
  *Default:* delete it in change #1, the way `Spine` was deleted when it was superseded.
- **Q5.6 — Encryption at rest for OAuth tokens (§5.5)** — in scope now, or explicitly deferred?
  *Default:* deferred, and noted in the change description so it isn't forgotten.

---

## 8. Next step

Answer §7.1–§7.4 (and §7.5 if you have views). Then this file becomes `docs/changes/google-account-integration/requirements.md` and `docs/changes/thunderbird-account-selection/requirements.md` — EARS notation, agreed before any `design.md`, per CLAUDE.md.
