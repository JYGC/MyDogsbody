# Pre-proposal — Invoices from Thunderbird to Google Calendar

**Status:** pre-proposal. Not a change folder yet — no `requirements.md` exists, and none should be written until the blocking questions in §7 are answered.
**Date:** 2026-08-08

---

## 1. What was asked for

Six user-facing pieces:

1. **An invoice page** with a table that lists every invoice found in one account of the Thunderbird profile folder, and a button that uploads them to a chosen Google account's calendar.
2. **A scan window on that page** — a chooser for how far back to scan the invoice source, in **days**: **7, 14, 30, 90 and 180 to begin with, with more addable on a settings page**. **14 days is the default**, and whatever you pick is **saved in the MyDogsbody database** so the page opens on it next time. The table shows what falls inside the chosen window, and changing it rescans.
3. **A diff in that same table** — for the selected calendar, show which invoices are already on it and which are not.
4. **A Google accounts page** for registering Google calendar integrations. Many Google accounts can be registered at once.
5. **A Thunderbird accounts page** that lists the accounts available in the profile folder and lets one be selected for import.
6. **A supplier templates page** where you maintain the **templates** that dictate how each supplier's mail is scanned for invoice fields. The number of suppliers cannot be known in advance, so templates are *data you edit at runtime*, not parsers someone writes in F#.

Everything below is a proposed shape for that, plus the decisions that have to be made before it can be specified. Nothing here is agreed except §1.1.

### 1.1 Answered so far — 2026-08-08

| Question | Answer | Consequence |
| --- | --- | --- |
| **Q1.1** — where the invoice data lives | **All of them.** PDF, DOC and text attachments, *and* the body of the email | Extraction is multi-format: one dependency type satisfied by four readers, picked by what the message actually holds. §3.2, §3.3 |
| **Q1.2** — which fields make an invoice | The proposed default — supplier, invoice number, due date, amount, currency, source message id | §3.2. "Supplier" is now a reference, not a string; due date turns out to be load-bearing, see Q1.10 |
| **Q1.3 / Q1.4** — how invoices are recognised, how many suppliers | **Unknowable in advance.** A user-maintained **template** per supplier says how to scan that supplier's mail | Extraction becomes a small rule engine over user data, plus a page to edit it. This is the largest single addition to the build — §3.7, §7.6 |
| **Q2.1 / Q2.2** — what lands on the calendar | An **all-day event on the due date**; title, description and no-reminder as proposed | Confirms due date is load-bearing — Q1.10 is now the only thing standing between an invoice and an event |
| **Q2.3** — which calendar | **A default invoice calendar chosen per Google account, on the Google accounts page** | Not a picker on the invoices page. The calendar becomes a property of the registered account, which moves `ListCalendars` into change #6 — §3.5 |
| **Q2.4** — what makes the diff "already there" | **A private extended property** on each created event, queried back with `privateExtendedProperty` | Survives renaming and moving the event. Also means the app only recognises events *it* created — see below |
| **Q3.3 / Q3.4** — where credentials live | **Remove the separate credentials integration.** Credentials go in a `Credentials` collection inside **the provider integration's own LiteDB** | The only answer here that deletes existing, working, tested code. It needs a change of its own — §3.9 |
| **Q4.1** — what "accounts" means | **The mail accounts configured in Thunderbird**, one selected at a time (per ask #5) | As proposed. No folder-level selection |
| **Q4.2** — how the profile is located | **You give a folder, and it is searched recursively** for accounts | Not `profiles.ini` discovery. Needs a folder picker, which is awkward from a BlazorWebView — §3.3, Q4.5 |
| **Q4.3** — mbox or maildir | **Both** | No "mbox first" phasing. Change #3 grows, and format detection becomes real work — §3.3 |
| **Q4.4** — is Thunderbird running | **Yes** | The store must be read without a clean lock |
| **Q4.5** — how the folder is chosen | **Native folder dialog** | `Microsoft.Win32.OpenFolderDialog` on the WPF side, injected as a `ChooseFolder` function. First change to touch the host — §5.12 |
| **Q4.6 / Q4.7** — how accounts are found, and may we read in place | **Settled by measuring the real profile** — see §3.8 | `prefs.js` is the mechanism, not a fallback; and at **16 GB**, copy-then-parse is impossible. Both were guesses before; neither is now |
| **Q4.8–Q4.11** — folders scanned, duplicates, incremental scanning, maildir | **All four as proposed** | Scan excludes Trash/Deleted/Junk/Sent/Drafts (6.2 GB, not 15.2); duplicate profiles listed qualified by path; per-folder watermarks with a full-rescan escape; maildir built against synthetic fixtures only. **§7.4 is now fully resolved** |
| **Thunderbird data retrieval** | **It is an Integration** | The mirror of the invoice/supplier rule. Accounts, folder lists, watermarks and the chosen root folder are Thunderbird's own facts, in Thunderbird's own store — never in the main database. §3.3 |
| **Q5.7** — do invoices really persist | **Yes.** The ledger is real | Confirms the reading §1.1 was built on. **Change #1 is unblocked** |
| **Q5.8** — what makes two scans agree | **Supplier + invoice reference** as the natural key, `SourceMessageId` for traceability, unique index in the migration | Rescanning an overlapping window updates rather than duplicates, and the database refuses a duplicate even if the code is wrong |
| **Q5.9** — where SQLite store functions live | **In `MyDogsbody.Database`**, which gains a `ProjectReference` to `MyDogsbody.Domain` | No new project. Outer-ring shape preserved: `handleError`, `Result<_, MyDogsbodyException>`, one `ActionNames` entry per function |
| **Q5.2 / Q5.3 / Q5.5** — testing and housekeeping | All as proposed | Google contract suites run against fakes + stubbed HTTP with live verification recorded as manual; synthetic mbox and invoice fixtures committed; `GoogleCalendarCRUD` deleted in change #6 |
| **Q5.1** — which storage tier | **Invoices are MyDogsbody items, not Integration items.** So are **suppliers** | The main SQLite database stops being theoretical: suppliers, templates and invoices all persist there, behind FluentMigrator migrations. §3.6 |
| **Scan window values** — supersedes Q1.8 | **Days, not months: 7 / 14 / 30 / 90 / 180 seeded, and the user can add more** on a settings page | The six-case union dies. A window is a **row**, so the set is runtime data like suppliers and templates, and the compile-time guarantee becomes a validation boundary. No fixed ceiling either, so `create` now needs a sanity bound — Q1.16. §3.2 |
| **Q1.7** — which window is selected on first open, and is it remembered | **14 days**, and the choice **persists in the main SQLite database** | Not in the Thunderbird store where the selected account lives — §5.16. This is the app's first real user setting and needs the first settings table — §3.6, Q5.13 |

#### What "MyDogsbody items, not Integration items" is taken to mean

Invoices and suppliers are the application's own concepts with their own lifecycles; the integrations are only where they came from and where they are pushed to. Concretely:

- The types live in `MyDogsbody.Domain/Invoices/` and `MyDogsbody.Domain/Suppliers/` and name nothing from Thunderbird, Google, mbox or MIME — already true in §3.2, and now the reason it must stay true.
- **Supplier is an entity, not a string field.** An invoice carries a `SupplierId` and the supplier record carries the name — which is what makes "every invoice from this supplier" answerable, and what a template hangs off. A free-text supplier name on each invoice would give you three spellings of the same company and no way to attach a template to any of them.
- **Suppliers, templates and invoices persist in the main SQLite database.** CLAUDE-project.md reserves the per-integration LiteDB stores for *an integration's own* data, and none of these are that. So this feature becomes the **first consumer of `MyDogsbody.Database`** — designed, migrated, never wired in — and adds the first real migrations alongside the `Blog`/`Comment` scaffold. Taken together the three tables are a small ledger, which is a fair description of what this app is becoming.
- `Integrations.Thunderbird` owns only Thunderbird's own facts: the profile path, which account is selected. It hands over messages and attachments and does not store, define or number invoices.
- `Integrations.Google` owns only Google's own facts: registered accounts, their default invoice calendar, and — per §3.9 — their **credentials**, in a `Credentials` collection in its own database.
- **An invoice outlives both.** Removing the Google account, or switching the Thunderbird account, does not delete invoices or suppliers.
- Reading a PDF is still an *integration* — it's an adapter for a capability the domain declares. Don't read this as moving `PdfDocumentReader` inward; the reading is infrastructure, the invoice is not.
- **Thunderbird data retrieval is an Integration, explicitly** — the mirror image of the rule above, and the two together draw the line cleanly. A Thunderbird *account* is not a MyDogsbody item: it is a fact about someone else's mail client, discovered by reading their files, meaningless if you uninstall Thunderbird. An *invoice* extracted from it is a MyDogsbody item and survives. So `Integrations.Thunderbird` owns the root folder, the discovered accounts, the folder lists and the scan watermarks, in its own LiteDB — and **none of those go anywhere near the main SQLite database**, which holds only suppliers, templates and invoices.

**Persistence is confirmed** (Q5.7): the ledger is real, and an invoice is a stored fact rather than a view recomputed from your mailbox. Everything in §3.6 follows from that, and it is what makes the 16 GB measured in §3.8 tolerable — you read mail once and keep what you found, rather than re-deriving it on every glance.

---

## 2. What already exists to build on

| Piece | Where | How it helps |
| --- | --- | --- |
| Credential store (LiteDB) | `MyDogsbody.Integrations.Credentials` | **Being removed** — §1.1 moves credentials into each provider's own database. Its store/mapper/context shape is still exactly what a provider's `Credentials` collection should copy. §3.9 |
| Credentials page + browser module + editor dialog | `UI.Portal/Pages/Settings/CredentialsPage.fs`, `Components/CredentialsComponents.fs` | The exact template for every new settings page: table + toolbar button + `FunComponent` dialog. Copy the shape even where the page itself is retired |
| `CredentialsBrowserModuleCreators` | `UI.Portal/ModuleCreators/` | The template for adaptive state: `cval` + `transact`, `startWork` as first parameter, write-then-reload, `ErrorAval` |
| PDF text extraction behind a domain dependency type | `MyDogsbody.Domain/Documents/`, `Integrations.Pdf/PdfDocumentReader.fs` | The pattern to extend: `ReadDocumentContent` exists with a contract suite. It is one of the **four** readers now needed, and its signature takes a file path — which attachments don't have. See §3.2 and Q1.11 |
| Google Calendar CRUD prototype | `GoogleCalendarCRUD/Program.fs` (scratch) | Working list/insert/update/delete against `CalendarService`, incl. the `GoogleWebAuthorizationBroker` + `FileDataStore` auth dance |
| `MyDogsbody.Integrations.Google` | one-line stub | The project to grow into the calendar adapter |
| Composition-root split | `Startup/CredentialApiMappers.fs`, `CredentialApiFactory.fs`, `Startup.fs` | The three-file shape each new API record must copy so it stays testable |
| Main SQLite database + FluentMigrator | `MyDogsbody.Database`, `.Database.Migrations` | Designed, migrated, **never wired into the app**. Per §1.1 this feature is now its **first consumer** — suppliers, templates and invoices all live here. §3.6 |

Two prototypes are *not* useful here: `TestMsGraphToEmails` is empty, and `GNUCashAccess` reads GnuCash XML (its `invoice` namespace is unrelated to email invoices).

There is **nothing** in the repo today that reads Thunderbird — no profile discovery, no mbox/maildir reader, no MIME parser. That is the largest unknown in this proposal.

---

## 3. Proposed shape

### 3.1 Flow

```
folder you chose ─────┐  Integrations.Thunderbird
  walked recursively  ├──►  ListMailAccounts
  prefs.js accounts   ├──►  ReadMailFolder account cutoff ──► messages + attachments
  mbox AND maildir    ┘     (read in place, Thunderbird is running)  │
                                                                     ▼
PDF / DOC / TXT ──────┐  Integrations.Documents                      │
  PdfPig              ├──►  ReadDocumentText — one type, four        │
  OpenXml / NPOI      ├──►  readers, chosen by the format the   ─────┤
  plain text + body   ┘     message actually carries                 │
                                                                     ▼
  ScanWindow picker ─┐                              Domain/Invoices  ExtractInvoicesWorkflow
  GetCurrentTime ────┴────────────────────────────────────────────►    │  cutoff = today - N days
                                                                       │  match supplier
main SQLite ──► LoadSuppliers, LoadTemplates, ────────────────────►    │  apply template (pure)
(MyDogsbody.Database)  LoadScanWindows, LoadSelectedScanWindow         │
            ◄── SaveInvoices, SaveSelectedScanWindow ◄─────────────────┤  StoredInvoice list
                                                                       ▼
Google Calendar ──────┐                       Domain/Calendar  DiffInvoicesWorkflow  (pure)
  Events.list         ├──►  ListCalendarEvents ────────────────►│
  Events.insert       ├──►  CreateCalendarEvent ◄───────────────┤  UploadInvoicesWorkflow
  CalendarList.list   ├──►  ListCalendars                       │
  OAuth token store   ┘  Integrations.Google                    ▼
                                                        InvoiceSyncApi (UI.Types)
                                                                 │
        UI.Portal   /invoices   /settings/suppliers   /settings/mail-accounts   …
```

### 3.2 Domain (`MyDogsbody.Domain`, references nothing — unchanged)

**Four** new workflow areas, each one folder, `<Area>Types.fs` first: `Suppliers/`, `InvoiceTemplates/`, `Invoices/`, `Calendar/`.

**`Suppliers/SuppliersTypes.fs`** — new, because a supplier is a MyDogsbody item
- `SupplierId`, `SupplierName` (constrained, non-empty, unique — uniqueness is the store's to enforce and the workflow's to report).
- `SupplierMatcher` — how a message is recognised as this supplier's. Sender address, sender domain, or a subject pattern; a supplier can hold several, and matching is "any of them".
- Stage types `UnvalidatedSupplier` → `ValidSupplier` → `StoredSupplier`, and `SupplierError` (`SupplierNameInvalid`, `SupplierNameTaken`, `SupplierNotFound`, `MatcherInvalid`).
- Dependency types: `LoadSuppliers`, `SaveSupplier`, `UpdateSupplier`, `DeleteSupplier`.

Keeping the matcher on the **supplier** rather than on the template is deliberate: "is this mail from Acme?" is a fact about Acme, while a template answers the different question "given it *is* Acme, where are the fields?". Splitting them lets one supplier own several templates — which you will want the first time a supplier changes their invoice layout — without repeating the matching rules in each.

**`InvoiceTemplates/InvoiceTemplatesTypes.fs`** — the rule model, written out in full in **§3.7**
- `TemplateId`, `DocumentPart`, `FieldRule`, `TargetField`, `ParseHint`, `TemplateFieldRule`, and the stage types `UnvalidatedTemplate` → `ValidTemplate` → `StoredTemplate`.
- `TemplateError` (`PatternInvalid`, `DateFormatInvalid`, `RequiredFieldHasNoRule`, `RuleTimedOut`) and the dependency types `LoadTemplatesForSupplier`, `SaveTemplate`, `UpdateTemplate`, `DeleteTemplate`.
- `ValidTemplate` is the type that matters: it can only be produced by validating an `UnvalidatedTemplate`, and `ApplyTemplateWorkflow` accepts nothing else. That is how a runtime-authored rule still gets a compile-time guarantee at the point it's used.

**`Invoices/InvoicesTypes.fs`**
- Constrained primitives: `InvoiceReference` (the supplier's own invoice number), `Money` (amount + currency), `DueDate`, `SourceMessageId`. **`SupplierName` is not here** — an invoice carries a `SupplierId`, per §1.1.
- **The scan window is a constrained number of days, and the set of windows is data** — not a closed union. Five values are seeded (7, 14, 30, 90, 180) and more can be added on a settings page, so "which windows exist?" is a question only the store can answer:

  ```fsharp
  /// A window is a row, not a case. The seeded values are a starting set, not the whole set, so
  /// the guarantee a closed union would have given moves into this create - the same move a
  /// user-authored template already forces (§5.9).
  type ScanWindowDays = private ScanWindowDays of int

  module ScanWindowDays =

      [<Literal>]
      let Minimum = 1

      [<Literal>]
      let Maximum = 3650                        // a typo guard, not a policy - Q1.16

      let create (days: int) : Result<ScanWindowDays, string> =
          if days < Minimum then Error "A scan window must be at least one day."
          elif days > Maximum then Error $"A scan window must be {Maximum} days or fewer."
          else Ok (ScanWindowDays days)

      let value (ScanWindowDays days) = days

      /// Seeded by migration, never hard-coded in a component.
      let seeded = [ 7; 14; 30; 90; 180 ]

      /// Used when nothing has been chosen yet, or the remembered choice no longer exists.
      let fallback = 14
  ```

  The usual three stage types follow: `UnvalidatedScanWindow` (what the number box held, a string) → `ValidScanWindow` → `StoredScanWindow { Id: ScanWindowId; Days: ScanWindowDays }`. They stay inside `Invoices/` rather than earning a fifth area — a window has no meaning outside a scan, and splitting it would have the invoice workflows depend on a sibling area for a value they own.

  **The remembered selection is stored as a number of days, not as a `ScanWindowId`.** An id would need a foreign key and a rule for what a deleted row does to it; a number survives its row being deleted, still means exactly what it meant, and simply isn't offered by the picker any more. See Q1.17.
- `ScanCutoff` — the instant computed from the window, `private ScanCutoff of DateTime`. Distinct from the window on purpose: a window is a *choice*, a cutoff is a *fact derived from the clock*, and the adapter must be handed the second so it cannot re-derive it differently. **Anchored to the start of the day** rather than to the moment of the click, so the same window scanned twice in one afternoon means the same thing both times — Q1.18.
- Stage types, now six, because persistence and the calendar each add one:

  | Stage | Shape | Meaning |
  | --- | --- | --- |
  | `ScannedMessage` | supplier id + normalised text lines + which part they came from | a message and its attachments flattened to text, ready for a template |
  | `UnvalidatedInvoice` | plain strings | what a template pulled out. untrusted |
  | `ValidInvoice` | constrained types, `DueDate option` | been through validation |
  | `StoredInvoice` | + `InvoiceId` | been through the store |
  | `UploadableInvoice` | `DueDate` **not** optional | an invoice that can actually become an event |
  | `SyncedInvoice` | + `CalendarEventId` | been put on a calendar |

  `UploadableInvoice` is how "an invoice with no due date can't go on a calendar" stops being a runtime check: the upload workflow accepts only that type, so an invoice missing a due date cannot reach it. The invoice is still stored and still listed — it just isn't uploadable, and the table says why. See Q1.10.
- Error DU: `InvoiceError` — `InvoiceReferenceInvalid`, `AmountUnparseable`, `MailStoreUnreadable of message`, `NoAccountSelected`, `SupplierNotRecognised of sender`, `NoTemplateForSupplier of SupplierId`, `TemplateMatchedNothing of fieldName`, `AttachmentUnreadable of filename * reason`, and three the editable window list adds: `ScanWindowInvalid of reason`, `ScanWindowAlreadyExists of days`, `CannotDeleteLastScanWindow`. The last one is a rule, not a guard — the picker must always have something to offer, so emptying the list is refused rather than handled downstream.
- Dependency function types: `ListMailAccounts`, `LoadSelectedMailAccount`, `SaveSelectedMailAccount`, and
  - `ReadMailFolder = MailAccountId -> ScanCutoff -> Result<MailMessage list, InvoiceError>` — the cutoff is a parameter so the adapter stops reading rather than reading everything and discarding. On a 180-day window over a large mbox that difference is the whole responsiveness of the page.
  - **`GetCurrentTime = unit -> DateTime`** — new, and required by the rules: CLAUDE.md forbids the domain reading a clock, and "N days back from today" needs one. Production binds `fun () -> DateTime.Now` at the composition root; every test binds a fixed instant, which is what makes the cutoff arithmetic assertable at all.
  - **`ReadDocumentText = DocumentSource -> Result<TextLine list, InvoiceError>`** — one type for all four formats, where

    ```fsharp
    type DocumentFormat = Pdf | Word | PlainText | EmailBody

    /// Bytes, not a path: an attachment lives inside an mbox, and it should not have to be
    /// spilled to a temp file (and cleaned up, and kept out of a backup) to be read.
    type DocumentSource = { Format: DocumentFormat; Name: string; Content: byte[] }
    ```

    The composition root binds one reader per format and dispatches on `Format`, so the domain sees a single function and adding a fifth format never touches a workflow. **This does not match the existing `ReadDocumentContent`**, which takes a `DocumentPath` and returns coordinate-bearing `Word`s — see Q1.11, it is a real decision, not a detail.
  - **The store, as functions**: `LoadInvoices = ScanCutoff option -> Result<StoredInvoice list, InvoiceError>`, `UpsertInvoices = ValidInvoice list -> Result<StoredInvoice list, InvoiceError>`, `MarkSynced = InvoiceId -> CalendarEventId -> Result<unit, InvoiceError>`. Upsert rather than insert, because rescanning the same window must not double the ledger — see Q5.8 for what makes two scans agree they found the same invoice.
  - **The scan windows, as functions**: `LoadScanWindows = unit -> Result<StoredScanWindow list, InvoiceError>`, `SaveScanWindow = ValidScanWindow -> Result<StoredScanWindow, InvoiceError>`, `DeleteScanWindow = ScanWindowId -> Result<unit, InvoiceError>`, plus the remembered choice: `LoadSelectedScanWindow = unit -> Result<ScanWindowDays option, InvoiceError>` and `SaveSelectedScanWindow = ScanWindowDays -> Result<unit, InvoiceError>`. The `option` is the honest shape — a fresh database has no choice recorded, and that is what `fallback` is for.

  From the `Suppliers` and `InvoiceTemplates` areas the invoice workflows also take `LoadSuppliers` and `LoadTemplatesForSupplier`. A workflow taking a dependency declared in a sibling area is fine — they are all inside `MyDogsbody.Domain`, and the alternative is duplicating the type.

**`Calendar/CalendarTypes.fs`**
- `GoogleAccountId`, `CalendarId`, `CalendarEventId`, `CalendarEvent`, and `InvoiceSyncKey` — the idempotency key, now settled as the value stamped into a private extended property (Q2.4). It is a domain type precisely because both sides of the diff have to derive it the same way.
- `AllDayEvent` — start date and title/description only. Q2.1 makes every invoice event all-day on the due date, so there is no reason for the domain to carry times, time zones or durations it never sets. Keeping them out means the mapper cannot accidentally invent one.
- `RegisteredGoogleAccount` carries its **default invoice calendar** (Q2.3): `{ Id; EmailAddress; DefaultInvoiceCalendar: CalendarId option }`. The `option` is not laziness — a freshly authorised account genuinely has no calendar chosen yet, and Q2.11 is about what the UI does in that state.
- Error DU: `CalendarError` — `CalendarUnreachable`, `NotAuthorised`, `AccountNotRegistered`, `NoDefaultCalendar of GoogleAccountId`, `CalendarNoLongerExists of CalendarId`, `EventRejected`, …
- Dependency function types: `ListGoogleAccounts`, `RegisterGoogleAccount`, `SetDefaultInvoiceCalendar`, `ListCalendars`, `ListCalendarEvents`, `CreateCalendarEvent`.

**One consequence of Q2.4 worth stating plainly:** a private extended property only exists on events *this app* created. An event you added to the calendar by hand for the same invoice carries no property, so the diff will not see it and the upload will create a second one. That is the correct trade for robustness against renaming — but it is the behaviour, so the page should not claim to detect "any event for this invoice".

**Workflows** (one file each, one public function each):
- **`ApplyTemplateWorkflow` — pure, no dependencies**: `Template -> ScannedMessage -> Result<UnvalidatedInvoice, InvoiceError>`. The rule engine. Everything that makes template editing worth having is decided here, and none of it touches a file, a clock or a network. Table-driven unit tests over `(template, text) → expected fields` are the cheapest coverage in the whole feature, and they'll be where template bugs actually get found.
- **`MatchSupplierWorkflow` — pure**: `StoredSupplier list -> ScannedMessage -> Result<SupplierId, InvoiceError>`. Also decides what happens when two suppliers match — Q7.6.4.
- **`ResolveScanWindowWorkflow` — pure**: `StoredScanWindow list -> ScanWindowDays option -> ScanWindowDays`. Given what exists and what was remembered, decide which window the page opens on. Three cases, one function, one place: nothing remembered → `fallback`; remembered and still in the list → that one; remembered but since deleted → `fallback`, or the shortest window still present if 14 has itself been deleted. Small enough to look not worth naming, and exactly the sort of rule that otherwise ends up half in a module creator and half in a mapper.
- `ScanForInvoicesWorkflow` — `getCurrentTime → readMailFolder → readDocumentText → loadSuppliers → loadTemplates → upsertInvoices → (account, ScanWindowDays) → StoredInvoice list`. The orchestration: work out the cutoff, pull the messages, flatten each to text, match a supplier, apply its template, validate, store. Note how much of it is calls to the two pure workflows above — that is the shape to aim for. The cutoff arithmetic (`getCurrentTime().Date.AddDays(-days)`) is a private pure function in this file, so "180 days back from 5 January" is a unit test with a fixed clock and no mail store anywhere near it. Days are uniform in a way months are not, which quietly removes the month-end trap flagged in §5.15.
- `DiffInvoicesAgainstCalendarWorkflow` — **pure**: `StoredInvoice list -> CalendarEvent list -> InvoiceSyncStatus list`. Still the heart of the feature and still the cheapest thing to test, provided the match key is a value and not a heuristic.
- `UploadInvoicesToCalendarWorkflow` — takes the diff result, calls `CreateCalendarEvent` for the missing ones only, and `MarkSynced` for each success.
- Supplier and template CRUD: `AddSupplierWorkflow`, `EditSupplierWorkflow`, `DeleteSupplierWorkflow`, `ListSuppliersWorkflow`, and the same four for templates. These are the same shape as the existing `AddCredentialWorkflow` / `EditCredentialWorkflow` and should be written by copying them.
- Scan-window CRUD, the same shape again but smaller: `AddScanWindowWorkflow` (rejects a duplicate and anything outside `create`'s bounds), `DeleteScanWindowWorkflow` (rejects deleting the last one), `ListScanWindowsWorkflow`, `SelectScanWindowWorkflow` (persists the choice). No edit workflow — a window is one number, so changing it is a delete and an add, and offering an edit would just be a second path to the same duplicate check.
- `ListMailAccountsWorkflow`, `SelectMailAccountWorkflow`, `RegisterGoogleAccountWorkflow`, `ListGoogleAccountsWorkflow`.

### 3.3 Outer ring

**`MyDogsbody.Integrations.Thunderbird`** (new) + `.Database.Models` (C#, if it stores anything)
- `ThunderbirdFolderScanner.fs` — **the entry point, per Q4.2.** Given the root folder you chose, walk it recursively for `prefs.js` files; each one found is a profile root. No `profiles.ini` lookup and no `%APPDATA%` assumption. Handles the folder being one profile, a parent of several, or a backup copy.
- `ThunderbirdAccountReader.fs` — `prefs.js` → accounts. **The authoritative list is `mail.accountmanager.accounts`, not the directory tree** — verified against the real profile in **§3.8**, where a directory walk would have found 15 IMAP accounts where 9 exist. Store paths come from `directory-rel`, never from `directory`, which was measurably stale.
- `MailFolderReader.fs` — **mbox *and* maildir, both required per Q4.3**, though the real profile is 100% mbox, so maildir ships against synthetic fixtures only (Q4.11). Format per account comes from `storeContractID`. MIME parsing (MimeKit is the realistic pick) for attachments. **Takes the cutoff and honours it while reading**: skip a message on its `Date` header before touching its body or attachments, so a 7-day window doesn't pay for a decade of mailbox.
- **Reads in place, never copies.** Thunderbird is running (Q4.4) and the store is 16 GB (§3.8), so `FileShare.ReadWrite` with a tolerated torn final message is the only workable read. Copy-then-parse — which I defaulted to before measuring — is off the table.
- **Scans incrementally.** Re-reading 6.2 GB of in-scope mail on every window change is not viable, so each folder carries a watermark and a scan reads only what has been appended since. §3.8, Q4.10.
- **Its own LiteDB store, holding everything Thunderbird-shaped**: the root folder you chose, the discovered accounts and their folder lists, the selected account, and the per-folder scan watermarks from Q4.10. Per §1.1 this is an Integration, so all of it stays here and none of it reaches the main SQLite database. The folder is no longer an "override" — after Q4.2 it is the only way the app finds anything, so the app is unusable until it is set, and the page must say so rather than showing an empty table.

As an integration it follows the same rules as `Integrations.Credentials` does today, and there is nothing novel to invent:

- A context record of `unit -> ILiteCollection<T>` getters from a `ThunderbirdDatabaseContextModule.getDatabaseContext`, with a `Dispose` that closes the `LiteDatabase`.
- Entities as mutable **C# classes** in `MyDogsbody.Integrations.Thunderbird.Database.Models`, because LiteDB needs settable properties.
- **A `BsonMapper.Global.ToDocument` warm-up per entity before the context is returned** — non-negotiable; CLAUDE-project.md records this as a 6-in-10 intermittent failure when it was missed, and a scan running on an `Async.Start` thread is exactly the reachable case.
- Adapters returning `Result<'T, MyDogsbodyException>`, written with `handleError`, one `ActionNames.MyDogsbody.Integrations.Thunderbird.*` entry per function.
- The collection getter stops at the integration boundary. `MyDogsbody.Domain` never names `ILiteCollection`, a profile path, an mbox offset or a `prefs.js` key — it names `ListMailAccounts` and `ReadMailFolder`, and this project satisfies them.
- It references `Domain` and nothing else outward. It does not know that invoices exist.
- **Hands over bytes, not paths.** An attachment is extracted from the message in memory and passed on as a `DocumentSource`; nothing is spilled to disk. Q1.11.

**`MyDogsbody.Integrations.Documents`** — the four readers behind `ReadDocumentText`
- `PdfDocumentReader.fs` — PdfPig. **Already exists**, in `MyDogsbody.Integrations.Pdf`, with a contract suite.
- `WordDocumentReader.fs` — `.docx` via DocumentFormat.OpenXml. Legacy `.doc` is a different and much worse problem — Q1.12.
- `PlainTextDocumentReader.fs` — decoding and line splitting; no library.
- `EmailBodyReader.fs` — the message body itself, which for HTML mail means stripping markup to text — Q1.13.

Whether this project *absorbs* `MyDogsbody.Integrations.Pdf` (rename, move the file, keep the tests) or sits beside it is Q1.14. Absorbing is my recommendation: one project per *capability* rather than per library keeps the composition root binding one dependency type in one place.

**`MyDogsbody.Integrations.Google`** (exists as a stub)
- `GoogleCalendarClient.fs` — `CalendarService` behind `ListCalendars` / `ListCalendarEvents` / `CreateCalendarEvent`, lifted from the `GoogleCalendarCRUD` prototype.
- `GoogleAccountStore.fs` — registered accounts, their default invoice calendar, and **their credentials**, all in `Google.db`: an `Accounts` collection and a `Credentials` collection in the provider's own database (§1.1, §3.9).
- `GoogleAuthorization.fs` — the consent flow (Q3.1).

Both follow the existing rules: `Result<'T, MyDogsbodyException>`, `handleError`, an `ActionNames` entry per function, the collection getter stopping at the integration boundary, and a `BsonMapper.Global.ToDocument` warm-up in any new LiteDB context.

### 3.4 Composition root

Six new API factories, each in the established three-file split with no module-level I/O outside `Startup.fs`:

- `SupplierApiFactory.fs` + `SupplierApiMappers.fs`
- `TemplateApiFactory.fs` + `TemplateApiMappers.fs`
- `MailAccountApiFactory.fs` + `MailAccountApiMappers.fs`
- `GoogleAccountApiFactory.fs` + `GoogleAccountApiMappers.fs`
- `InvoiceSyncApiFactory.fs` + `InvoiceSyncApiMappers.fs`
- `ScanWindowApiFactory.fs` + `ScanWindowApiMappers.fs` — small, and kept separate on purpose: it is consumed by *two* surfaces (the settings page that maintains the list, and the invoices page's picker), and neither of them should have to take the whole invoice API to read five numbers

And **one pair deleted**: `CredentialApiFactory.fs` + `CredentialApiMappers.fs`, per §3.9.

`Startup.fs` gains the SQLite context, the provider LiteDB handles and the `GetCurrentTime` binding, loses the credentials context, and ends with six registrations instead of one. `MainWindow.xaml.cs` still does not change — that is the property worth preserving through all of this.

### 3.5 UI

**`MyDogsbody.UI.Types`** — new records (no domain types leak here):
- `GoogleAccountUiType`, `CalendarUiType`, `MailAccountUiType`, `SupplierUiType`, `TemplateUiType`, `FieldRuleUiType`, `InvoiceUiType` (with a `SyncStatus` field and a resolved supplier name — the UI shows a name, the domain holds an id), and the API records `GoogleAccountApi`, `MailAccountApi`, `SupplierApi`, `TemplateApi`, `InvoiceSyncApi`, `ScanWindowApi`.
- **`ScanWindowUiType`** — now a plain record, `{ Id: string; Days: int; Label: string }`, because the domain type it mirrors is no longer a union either. An earlier draft argued for mirroring a six-case union here so the compiler could reject a seventh value; **that argument is dead**, and worth being explicit about rather than quietly dropping. Making the set user-editable moves the "is this a legal window?" check out of the compiler and into `ScanWindowDays.create`, and the mapper becomes trivial in both directions. What is bought: the app no longer needs a rebuild to gain a window. What is paid: an illegal number is now a runtime `Error`, caught at one boundary, and only tests can prove that boundary holds. Same trade as templates (§5.9), one tier smaller.
  `Label` is the mapper's business, not the domain's — "14 days", "6 months" for 180 if you want it — so the component renders a string it was handed rather than composing one.
- `Modules/GoogleAccountsBrowserModule.fs`, `Modules/MailAccountsBrowserModule.fs`, `Modules/ScanWindowsBrowserModule.fs`, `Modules/InvoiceSyncModule.fs` — each a record of `aval` fields + commands, same shape as `CredentialsBrowserModule`. `InvoiceSyncModule` carries `SelectedScanWindowAval: aval<ScanWindowUiType>`, `AvailableScanWindowsAval: aval<ScanWindowUiType list>` and `SelectScanWindow: ScanWindowUiType -> unit`; selecting one **persists the choice and then rescans**, which is write-then-reload exactly like an edit, so the table always shows what was actually scanned rather than what the picker was holding. The initial value comes from `ResolveScanWindowWorkflow` through the API, never from a literal `14` in the component.

**`MyDogsbody.UI.Portal`** — six pages, registered in `Shell.fs`; the five settings pages also linked from `SettingsComponents.settingsNavMenu`:

| Page | Route | Contents |
| --- | --- | --- |
| Invoices | `/invoices` (top-level, not settings) | **Scan-window picker, rendering whatever windows the store holds and opening on the remembered choice**, Google-account picker — **no calendar picker**, the account carries its own (Q2.3), shown read-only beside it — refresh, invoice table with a per-row sync-status column, "Upload to calendar" button, `MudAlert` from `ErrorAval` |
| Google accounts | `/settings/google-accounts` | Table of registered accounts, "Add account" → consent flow, **the default invoice calendar for each account, picked from a dropdown of that account's own calendars**, remove / re-authorise |
| Thunderbird accounts | `/settings/mail-accounts` | **The root folder to search** (Q4.2) with a Browse button, a "scan for accounts" action, the table of accounts the recursive walk found — name, email, store format, message count — and a radio to pick the one to import from |
| **Suppliers** | `/settings/suppliers` | Table of suppliers with their match rules; add / edit / delete via dialog; each row opens that supplier's templates |
| **Templates** | `/settings/suppliers/{id}/templates` | The templates for one supplier: field rules, and — strongly recommended — a **"test against a message" panel** that runs `ApplyTemplateWorkflow` over a real scanned message and shows what each rule extracted. See §3.7 |
| **Scan windows** | `/settings/scan-windows` | The list of windows in days — 7, 14, 30, 90 and 180 seeded — with add and delete, the currently remembered one marked, and the last remaining one undeletable. One number field per row and no edit action, per §3.2 |

Both new settings pages are the credentials page again: `MudTable` + toolbar button + `FunComponent` dialog + module creator with `cval`/`transact`. Nothing novel, which is the point — the novelty is all in §3.7.

The scan-window picker was going to be a `MudToggleGroup` of six fixed buttons. **It can't be, now that the count is unknown at build time** — five looks fine as buttons and twelve does not, and the component cannot know which it will get. So: a `MudSelect` bound to `AvailableScanWindowsAval`, which renders any number of windows without the toolbar deciding how many are reasonable. The component holds no list of its own either way — that part of the original argument survives, and matters more now that the list is genuinely variable.

### 3.6 Persistence — the main SQLite database, finally wired in

§1.1 makes suppliers, templates and invoices MyDogsbody items, so they go in `MyDogsbody.Database`. This is the change CLAUDE-project.md anticipated: *"the first change that needs the main database adds one in `Startup/Startup.fs` alongside the LiteDB contexts, writes store functions against it, and binds those to the dependency function types a workflow declares."*

**Tables** — each with its own FluentMigrator migration under `Migrations/Migration_<timestamp>_<Name>.fs`, in timestamp order above `MigrationSetup.fs`:

| Table | Holds |
| --- | --- |
| `Suppliers` | id, name |
| `SupplierMatchers` | supplier id, kind (sender / domain / subject), value |
| `InvoiceTemplates` | id, supplier id, name, which document part it applies to, ordering |
| `TemplateFieldRules` | template id, target field, rule kind, pattern/label, parse hint |
| `Invoices` | id, supplier id, reference, amount, currency, due date (nullable), source message id, scanned date |
| `InvoiceCalendarEvents` | invoice id, google account id, calendar id, event id, uploaded date |
| `ScanWindows` | id, days — **unique**, so 14 cannot be added twice |
| `InvoiceSettings` | a single row with its primary key fixed at 1 — the remembered scan window, in days |

`Blog` and `Comment` stay as they are — they're scaffold, and nothing here disturbs them.

**The five seeded windows are inserted by the migration that creates the table**, with `Insert.IntoTable`, and removed again by its `Down`. That keeps the rule CLAUDE-project.md states — the schema, and now its seed data, come from FluentMigrator and from nowhere else — rather than having `Startup.fs` check on every launch whether it ought to write five rows. The consequence to accept knowingly: if you delete a seeded window, re-running migrations will not bring it back. That is the correct behaviour for a value you chose to remove, and it is why `ScanWindowDays.fallback` exists rather than the code assuming 14 is present.

**`InvoiceSettings` is a single-row typed table, not a key/value store** — see Q5.13, because this is the first setting and whatever it does becomes the habit. A `Settings(Key, Value)` table would take one migration and then never need another, at the cost of every setting being a string parsed at the point of use; a typed row costs a migration per setting and keeps the store honest about what it holds. The codebase's whole instinct is the second, and adding a column by migration is already the rule for every other schema change here.

**`InvoiceCalendarEvents`** is the table worth arguing about. A sync record is a fact about *an invoice*, so it belongs on this side rather than in the Google integration's store — but it also means the app has an opinion about what's on a calendar that can go stale if you delete an event by hand. **The calendar remains the source of truth for the diff** (Q2.4's extended-property query), and this table is history: what we uploaded, when, to which account. Don't let it become the answer to "is it there?".

**The store functions live in `MyDogsbody.Database`** (Q5.9): it gains `SupplierStore.fs`, `TemplateStore.fs`, `InvoiceStore.fs`, `ScanWindowStore.fs` and their record ⇄ domain mappers, plus a `ProjectReference` to `MyDogsbody.Domain`. No new project. It is outer-ring code, so it keeps the outer-ring shape: dependencies first, input last, `Result<'T, MyDogsbodyException>`, written with `handleError`, one `ActionNames` entry per function.

**`Invoices` carries a unique index on (supplier id, invoice reference)** (Q5.8). That pair is the natural key, so a rescan of an overlapping window updates rather than duplicates, and the database refuses a duplicate even when the code is wrong. `SourceMessageId` rides along for traceability but is not the key — one message can carry more than one invoice.

**The mapper count does not go up.** Still exactly two hops per feature: SQLite record ⇄ domain at the bottom, domain ⇄ UI record at the top. If a third record with the same fields appears, something has gone wrong.

Every SQLite integration test builds its schema by calling `MigrationSetup.setupMigrations` against a fresh temp file — never hand-written DDL — so each of these tests doubles as a migration test, and each migration also gets its own `Up`/`Down` test.

### 3.7 What a template actually is

This is the part with no precedent in the codebase and the most room to get wrong, so it deserves its own design pass before any code. A template is **data the user types**, so its expressive power is a product decision, not an implementation detail.

The shape I'd propose for a first version:

```fsharp
type DocumentPart = Body | Attachment of DocumentFormat | AnyPart

/// How one field is located in the text. Deliberately small — every case here is one the
/// template page has to render an editor for, and one the user has to understand.
type FieldRule =
    | AfterLabel of label: string                    // "Invoice No:" → the rest of that line
    | LinesAfterLabel of label: string * offset: int // the value sits below the label
    | RegexCapture of pattern: string                // first capture group
    | FixedValue of string                           // e.g. currency that never varies

type TargetField = Reference | Amount | Currency | DueDate

type ParseHint =
    | AsText
    | AsMoney of decimalSeparator: char
    | AsDate of format: string                       // "dd/MM/yyyy"

type TemplateFieldRule = { Field: TargetField; Rule: FieldRule; Hint: ParseHint }
```

A `Template` is a supplier id, a `DocumentPart`, an ordering, and a list of `TemplateFieldRule`. `ApplyTemplateWorkflow` runs the rules against a `ScannedMessage` and returns an `UnvalidatedInvoice` — pure, total, and trivially table-testable.

Four things this raises now rather than later:

1. **Regex from a text box is an availability risk.** A user-written pattern can backtrack catastrophically and hang the scan on a large mailbox. If `RegexCapture` survives into v1, the engine must construct every `Regex` with a `matchTimeout` (and ideally `RegexOptions.NonBacktracking`), and a timeout must surface as a named error against that rule — not as a frozen UI. This is a hard requirement, not a nicety.
2. **A template must be validated when it is saved**, not when a scan runs at midnight: the regex compiles, the date format is a real format string, every required field has a rule. That is the `ValidTemplate` type, and it's what the page's Save button produces.
3. **Blind editing is miserable.** Without a "test against this message" panel, you're writing regexes against text you can't see. I'd treat that panel as core to the templates page rather than a follow-up — it's also the cheapest possible acceptance test for the whole extraction path.
4. **Supplier name is not extracted.** It comes from the supplier record the matcher chose. There is no `TargetField.Supplier`, deliberately.

§7.6 is the question set for all of this.

### 3.8 Extracting accounts — a plan verified against the real profile

Measured against `C:\Users\jygcn\AppData\Roaming\Thunderbird\Profiles\49stkd1y.default` on 2026-08-08. Everything below is what that profile actually contains, not what the format documentation says it should.

#### What the profile turned out to hold

| | |
| --- | --- |
| Accounts configured | **10** — 9 IMAP + Local Folders |
| Directories under `ImapMail/` | **15** |
| **Orphan directories** from deleted accounts | **6**, one of them holding a 2 GB `Deleted` file |
| Store format | **All 10 `berkeleystore` (mbox). Zero maildir** |
| Total mail store | **16 GB** (`ImapMail` 16 GB, `Mail` 267 MB) |
| Largest single mbox file | **2.5 GB** (`imap.googlemail-1.com/[Gmail].sbd/Trash`) |
| mbox files in total | **599** |
| In `Trash`/`Deleted`/`Junk`/`Sent`/`Drafts` | **9.0 GB** |
| Everything else | **6.2 GB** |

#### Three findings that change the design

**1. `prefs.js` is the mechanism, not a fallback.** Q4.6 offered structural detection as an alternative. On this profile it would find 15 IMAP "accounts" where 9 exist — a 60% false-positive rate — because deleted accounts leave their directories behind, one with 2 GB still in it. Worse, directory names are disambiguated with a numeric infix (`imap.googlemail.com`, `imap.googlemail-1.com`, `-2`, `-3` are four different Google accounts on one host), so a directory name is neither the hostname nor the account. **Discovery reads `prefs.js`. A directory with no account pointing at it is ignored.**

**2. `mail.server.serverN.directory` is stale; use `directory-rel`.** In this profile the absolute path records `C:\Users\JunYing\...` while the profile actually lives at `C:\Users\jygcn\...` — the Windows account was renamed at some point and Thunderbird never rewrote it. The relative form is correct:

```
mail.server.server2.directory     = C:\Users\JunYing\...\Mail\Local Folders   ← wrong
mail.server.server2.directory-rel = [ProfD]Mail/Local Folders                 ← right
```

Resolve `[ProfD]` against **the folder the user chose**, never against the recorded absolute path. This also happens to be what makes a copied or relocated profile work at all, which is the case Q4.9 is about.

**3. At 16 GB, copy-then-parse is impossible.** That was my default for Q4.4 and it is simply wrong at this scale — a single 2.5 GB mbox cannot be copied per scan, let alone 16 GB. The reader **must** open with `FileShare.ReadWrite` and read in place, tolerating a torn final message. There is no fallback to argue about.

It also made **the folder exclusions load-bearing rather than tidy** — dropping Trash, Deleted, Junk, Sent and Drafts removes 9.0 GB of 15.2 GB, the difference between a scan that is feasible and one that is not. That is now decided (Q4.8): **the scan covers 6.2 GB, not 15.2.**

And it forces something not previously in this proposal — **incremental scanning**. Re-reading 6.2 GB every time the window picker changes is not viable, so each folder needs a watermark (file size and mtime at last scan, plus the offset reached) and a scan must read only what has been appended since. mbox is append-only in normal operation, which is what makes this sound; a compact or a repair resets the watermark and forces a full re-read of that folder. **Q4.10.**

#### The algorithm

Given the folder the user chose:

1. **Walk recursively for `prefs.js`.** Each one found is a profile root; `[ProfD]` for its accounts resolves to the directory containing it. This satisfies Q4.2's "search recursively" while keeping every path resolution correct, and handles the chosen folder being one profile, a parent of several, or a backup.
2. **Read `mail.accountmanager.accounts`** — an ordered CSV of account keys, and the authoritative list. In this profile it holds 10 keys while `mail.account.lastKey` is 20 and the numbering has gaps (no `account4`, `5`, `7`, `8`, `11`–`16`). **Never iterate `1..lastKey`.**
3. **For each account key**, read `mail.account.<key>.server` → `serverN`, and `mail.account.<key>.identities` → a CSV of identity keys (`account18` here has two: `id10,id11`).
4. **For each `serverN`**, read `type` (`imap`, `pop3`, `none` for Local Folders), `hostname`, `userName`, `name` (the display name — usually the email address), `storeContractID` (`berkeleystore` = mbox, `maildirstore` = maildir) and `directory-rel`.
5. **For each identity**, read `mail.identity.<id>.useremail` and `.fullName`. That is the address a supplier matcher will compare against, and an account can have more than one.
6. **Resolve the store directory** from `directory-rel`, and confirm it exists. If it does not, report the account as configured-but-missing rather than dropping it silently.
7. **Enumerate folders** inside the store: an extensionless file is a folder's mbox, a sibling `.sbd` directory holds its children, and nesting repeats to arbitrary depth (`Music.sbd/Surrey Hills Orchestra.sbd/Messages` exists here). **Ignore `.msf` entirely** — it is Mork, and it is not a reliable index of what exists: this profile has `Archives.msf` and `Drafts.msf` with no corresponding mbox file at all.

Steps 1–6 are pure parsing over text files and a directory listing. They are fast, they touch no mail, and they are exactly what the mail accounts page needs to render its table — which means the page can be built and tested before a single message is read.

### 3.9 Credentials move into the provider integrations

Per §1.1, **`MyDogsbody.Integrations.Credentials` goes away.** There is no shared credential store and no `Credentials.db`. Instead each provider integration owns a `Credentials` collection inside **its own** LiteDB database — Google's tokens live in Google's database, next to Google's registered accounts.

This is the only decision so far that deletes working, tested code, so it is worth being precise about what it touches:

| Goes | Arrives |
| --- | --- |
| `MyDogsbody.Integrations.Credentials` (`CredentialStore.fs`, `CredentialEntityMappers.fs`, the context module) | `Credentials` collection + getter on each provider's existing context record |
| `MyDogsbody.Integrations.Credentials.Database.Models` (C# `Credential` entity) | A credential entity per provider, in that provider's `.Database.Models` |
| `Credentials.db` in the working directory | Nothing — the rows live in `Google.db` and friends |
| `Startup.fs`'s credential context and `credentialApi` binding | Per-provider API records, bound the same way |
| Possibly `MyDogsbody.Domain/Credentials/` and its three workflows | Q3.7 |
| Possibly `/settings/credentials`, `CredentialsComponents`, `CredentialsBrowserModule*` | The Google accounts page, which is already the place you manage Google's credentials |

**The database is the provider, so the discriminator field goes.** This is the same argument CLAUDE-project.md already makes for the log store — *"the collection is the severity, do not add a `Severity` field"* — applied one tier up. A credential in `Google.db` is a Google credential; an `InfrastructureType` column beside it would be a second source of truth for a fact the file path already states. That in turn makes `MyDogsbody.Enums` (which exists only to share `InfrastructureType` between F# and C#) possibly redundant, and the domain's own `Infrastructure` union along with it — see Q3.7 and Q3.8.

**This must be its own change, and it must go first among the Google work.** It modifies code that is currently green at all four test levels, and CLAUDE.md is explicit: *"Existing behaviour you depend on but are not changing gets a characterization test before you change anything near it."* Folding this into `google-account-integration` would mean a change that simultaneously deletes a store, moves a UI page, rewires the composition root and adds OAuth — with no clean point to check the suite. Split, it is a boring refactor followed by an ordinary feature.

---

## 4. Proposed change breakdown

One change folder for all of this would be too large to review or to test in the CLAUDE.md sense. Suggested sequence — each is independently shippable and each gets its own `docs/changes/<name>/`:

| # | Change | Delivers | Depends on |
| --- | --- | --- | --- |
The answers in §1.1 roughly doubled this, so it is now seven:

| # | Change | Delivers | Depends on |
| --- | --- | --- | --- |
| 1 | `invoice-ledger-foundation` | **The main SQLite database wired into the app for the first time**: `Suppliers` + `SupplierMatchers` migrations, store functions, `SupplierApi`, suppliers page. No invoices, no mail, no calendar | — |
| 2 | `invoice-templates` | The template model (§3.7), `ApplyTemplateWorkflow`, template migrations, templates page with the rule editor and the test panel. Ask #6 | 1 |
| 3 | `thunderbird-account-selection` | Thunderbird accounts page, native folder picker (first host change), recursive `prefs.js` discovery per §3.8, both mbox and maildir, selection persisted. Grew with the Q4.2–Q4.4 answers. Ask #5 | — |
| 4 | `invoice-extraction` | The four document readers, the `Invoices` migration, the scan pipeline, a read-only invoice table — **and the whole scan-window apparatus**: the `ScanWindows` and `InvoiceSettings` migrations with their seed, the settings page, the picker, the remembered choice. Ask #2 | 1, 2, 3 |
| 5 | `credentials-per-provider` | **Pure refactor, no new feature.** Deletes `Integrations.Credentials`; each provider integration gains a `Credentials` collection in its own LiteDB. Characterization tests before anything moves. §3.9 | — |
| 6 | `google-account-integration` | Google accounts page, OAuth registration, calendar listing, **and the per-account default invoice calendar** — Q2.3 moved that here from #7. Ask #4 | 5 |
| 7 | `invoice-calendar-sync` | The diff, the sync-status column, the upload button, `InvoiceCalendarEvents`. Asks #1 and #3 | 4, 6 |

The scan-window work could technically move into change #1 — it is two small tables, a store and a settings page, all of it main-database machinery that #1 exists to prove. It stays in #4 because a window with nothing to scan is a setting that does nothing, and #1's value is being the smallest change that proves SQLite works. If #4 turns out too large once §7.1 is answered, this is the piece to move.

1, 3 and 5 are independent starting points. Change 5 is the odd one out — it is the only change here that *removes* something rather than adding, and it is the only one whose success criterion is "the suite is still green and one fewer project exists". **Change 1 is the one I'd start with regardless of the rest**: it is small, it has no external dependency to negotiate, and it is the change that proves the main database, its migrations and its store shape actually work — every later change leans on that and none of them wants to discover a problem with it.

The path 1 → 2 → 3 → 4 gives a working invoice ledger, with your own templates, over your own mail, with no Google involvement at all. That is also the cheapest way to find out whether the templates in §3.7 are expressive enough for your real suppliers — which is the largest unproven assumption in this proposal, and worth testing before any calendar code exists.

---

## 5. Where this rubs against the current architecture

These are real, and each needs a decision — they are not blockers, but pretending they aren't there will cost a rewrite.

1. **The Google client is async; domain workflows are synchronous `Result`.** The prototype uses `.Result`, which blocks. Options: keep blocking (calls already run off the render thread via `startWork`), or take the **FsToolkit.ErrorHandling** dependency for `asyncResult`, which CLAUDE-project.md already contemplates for exactly this case. Recommendation: blocking in change #6, revisit if the upload batch in #7 makes the UI feel stuck.
2. **Contract tests against a network service.** CLAUDE.md requires each dependency function type's shared suite to run against the real adapter *and* every fake. For `CreateCalendarEvent` the real adapter is Google. Realistic answer: run the suite against the fakes plus a recorded/stubbed `HttpMessageHandler`, and record live verification as manual coverage in the change description. This must be stated explicitly rather than quietly skipped.
3. **Thunderbird files may be locked or mid-write** while Thunderbird itself is running. Reading mbox under a live lock is a genuine failure mode, not an edge case — it needs a named `InvoiceError` case and a sentence on screen.
4. **`.msf` index files are Mork format** — do not parse them. Either read the mail store directly, or read `global-messages-db.sqlite` (gloda), which is a SQLite index but is not guaranteed to be enabled or current.
5. **Secrets at rest.** The existing credential store persists secrets in plaintext LiteDB. OAuth refresh tokens are materially worse to leak than the current contents. If this feature should encrypt (DPAPI is the low-friction Windows answer), say so now — retrofitting encryption over stored tokens is its own change.
6. **`Startup.fs` opens its databases at module load.** Three more stores means three more files opened in the working directory on first touch. Fine, but tests must keep away from `Startup` exactly as they do today.
7. **The existing `Documents` area no longer fits.** `ReadDocumentContent` takes a `DocumentPath` and returns coordinate-bearing `Word`s; attachments have no path and three of the four formats have no coordinates. Something has to give, and there are consumers: `ReadDocumentLinesWorkflow`, the contract suite, and the `PdfProcessing` scratch project. Options are to widen the existing type, or to add `ReadDocumentText` beside it and let the PDF reader satisfy both. Q1.11.
8. **Legacy `.doc` is a materially different problem from `.docx`.** OOXML is a zip of XML and DocumentFormat.OpenXml reads it in a few lines; the pre-2007 binary format needs NPOI or similar and is genuinely unpleasant. If your suppliers only ever send `.docx`, this stays small. Q1.12.
9. **A user-editable rule engine sits awkwardly with "types carry the rules".** The codebase's instinct is to make invalid states unrepresentable at compile time; a template is typed in at runtime, so the guarantee has to move to a validation boundary — `ValidTemplate`, produced when the page saves and never constructed anywhere else. That's the right answer, but it is a weaker guarantee than the rest of the domain enjoys, and the tests have to carry more of the weight. Add the regex-timeout requirement from §3.7 and this is the riskiest area in the build.
10. **Change #5 deletes tested, working code, and coverage goes down before it goes up.** Removing `Integrations.Credentials` takes three domain workflows, a store, two mappers, a UI page and their tests out of a suite that is currently 204 green tests. That is the correct outcome — code that no longer exists needs no tests — but the change must say plainly what was removed rather than letting the total quietly drop. Characterization tests over the behaviour being *preserved* (a credential round-trips, a secret survives storage unchanged) go in first and are what the new per-provider collections must satisfy.
11. **The main database has never actually been run by the application.** `MigrationSetup` has no caller, there is no composition-root binding, and `createDatabaseContext` opens a `SqliteConnection` it never disposes — which the test guidance already warns keeps temp files locked on Windows. Change 1 is where all of that gets exercised for real for the first time. Expect to find something.
12. **The folder picker forces the first change to the WPF host** — Q4.5 chose the native dialog, so this is now a fact rather than a risk. Blazor cannot supply a filesystem path: `InputFile` hands over content, not locations, and `webkitdirectory` is no better inside a `BlazorWebView`. It means `Microsoft.Win32.OpenFolderDialog` on the WPF side, exposed as an injected `ChooseFolder: unit -> string option` — satisfied by WPF in production and a lambda in tests. A small, clean seam, but it does end the run of changes in which `MainWindow.xaml.cs` never had to be touched. Worth noting in change #3's description rather than slipping it in.
13. **Recursively walking a folder you chose is not the same as reading a known profile path.** It can be enormous, contain several profiles or none, hit directories the process cannot read, and on Windows follow junctions into a loop. The scanner needs a depth bound, permission errors reported per-directory rather than aborting the walk, and a result that distinguishes "no accounts here" from "I could not look". §3.8 adds a concrete case: this profile holds **six orphan mail directories from deleted accounts**, one with 2 GB in it, so "found a mail store" and "found an account" are genuinely different answers.
14. **The mail store is 16 GB, and that is a design input rather than a footnote.** It kills copy-then-parse outright, it makes Q4.8's folder exclusions load-bearing (they remove 9 of 15.2 GB), it forces incremental scanning (Q4.10), and it puts real pressure on Q1.9's rescan-on-every-click. It also strengthens Q5.7: re-deriving invoices from 6 GB of mail on demand is a poor substitute for storing them once. Any performance assumption in this proposal should be checked against that number rather than against a test mailbox.
15. **The clock is a new dependency function type, and CLAUDE.md calls those published interfaces** — meaning `GetCurrentTime` owes a contract suite run against the real implementation *and* every fake. The real implementation is `DateTime.Now`, whose whole nature is to return something different each call, so "assert both sides agree" has no obvious meaning. Workable answer: the suite asserts the properties that must hold of any clock (monotonic across two calls, `Kind` as expected, within a tolerance of `DateTime.Now` at the point of test) and the *cutoff arithmetic* — the part with actual logic — is unit-tested against fixed instants in the workflow. Say this explicitly in the change rather than quietly having no contract test for the clock. Moving the window to **days** removes the trap this bullet used to warn about — `.AddMonths(-1)` from 31 March gives 28 February, and nobody expects that — because days are uniform and `.AddDays(-14.0)` cannot surprise anyone. What replaces it is smaller but real: `DateTime.Now` is local, so a window measured from the exact instant of the click quietly means "N×24 hours ago, at whatever time it is now", and the same window scanned at 09:00 and at 17:00 covers different mail. Anchoring the cutoff to `.Date` fixes that and makes the value stable for the whole day, which incremental scanning (Q4.10) also prefers. **Q1.18.**

16. **Settings are now split across two stores, by rule rather than by accident.** The selected *mail account* lives in the Thunderbird integration's own LiteDB, because §1.1 makes it Thunderbird's own fact. The selected *scan window* lives in the main SQLite database, because you said so and because it is a preference about the invoices page rather than about anyone's mail client. Both placements follow the ownership rule; together they mean there is no single "settings" store to point at, and the next preference will need the same judgement made again. The rule is what belongs in the change description — not the location.
17. **Seeding rows from a migration is a new precedent in this repo.** Every migration so far creates schema and nothing else. The five default windows have to come from somewhere, and the alternatives are worse: `Startup.fs` checking on each launch whether it ought to write five rows is runtime schema management by another name, and hard-coding them in the component is the thing this whole change is undoing. `Insert.IntoTable` in `Up`, matching `Delete.FromTable` in `Down` — but say plainly in change #4 that the file now carries data as well as structure, because the next person will copy whichever migration they open first.

---

## 6. What would be true when it's done

- `dotnet build MyDogsbody.sln` clean; `dotnet test` green with zero skips, across all four levels — with the one honest exception in §5.2 declared in the change description.
- `MyDogsbody.Domain` still has zero `ProjectReference` elements (`AssertDomainReferencesNothing` + `Contracts/DomainIsolationTests.fs` both still pass).
- Still exactly two mapping points per feature: entity ⇄ domain in each integration, domain ⇄ UI record in `Startup/*ApiMappers.fs`.
- `UI.Portal` still references only `UI.Types`, `Enums`, `Exceptions.Types`.
- Uploading twice in a row creates no duplicate events.
- **Scan windows exist as rows and nowhere else.** No list of days in a component, a mapper or a union; the seeded five arrive from a migration and the picker renders whatever the store holds. **Adding a sixth window is done on screen and takes effect without a rebuild** — the same acceptance test as a supplier or a template, one tier smaller.
- **The picker opens on 14 days against a fresh database, on your last choice on every run after that**, and on the fallback if the window you last chose has since been deleted. That third case has a unit test, because it is the one nobody thinks to try by hand.
- Deleting the last remaining scan window is refused with a named error, so the picker can never be empty and no component needs an "if the list is empty" branch.
- No workflow reads the clock directly; every cutoff test pins a fixed instant and asserts an exact date. The same window scanned twice in one day produces the same cutoff both times.
- **Adding a supplier and teaching the app to read its invoices is done entirely on screen** — no rebuild, no F# change, no restart. That is the whole point of ask #6, and it is the acceptance test for it.
- Suppliers, templates and invoices live in the main SQLite database, with the schema built **only** by FluentMigrator — no DDL in a store function, a test, or a SQLite tool.
- A template carrying a pathological regex fails *that rule* with a named error inside its timeout, and the scan finishes. It does not hang the page.
- `MyDogsbody.Domain` still names no Thunderbird, Google, LiteDB, SQLite, MIME or PDF type — the ledger got bigger, the centre did not get less pure.
- **A scan completes with Thunderbird open**, against both an mbox account and a maildir one, without Thunderbird noticing and without corrupting anything. Reading a live 16 GB mail store is the one operation here that could damage data that isn't ours, so it is read-only by construction — opened for read with `FileShare.ReadWrite`, never copied, never written — and tested that way.
- **The accounts page lists exactly the 10 accounts `prefs.js` declares** for the profile in §3.8 — not the 15 directories under `ImapMail/`, and not the six orphans left by deleted accounts. That number is the acceptance test for discovery.
- **No `Credentials.db` and no `MyDogsbody.Integrations.Credentials`.** Each provider's credentials sit in that provider's own database, and nothing outside a provider integration opens it. The solution has one fewer project than it does today, possibly two once `MyDogsbody.Enums` goes (Q3.8).

---

## 7. Questions to answer

Answer the **blocking** ones before the `requirements.md` for the change they block. The rest can be decided during design, but earlier is cheaper. Each carries my recommendation — "default" is what I'd write if you just said "use your judgement".

**Answered questions are removed from this section** — the decisions they became are recorded in §1.1 and built into §3. What is left below is only what is still open: **42 questions**. **§7.4 is empty**, so **changes #1 and #3 can both be specified today** — see §8. §7.6 is the set that still gates the largest piece of the build. Numbering is not contiguous; gaps are answered questions, and numbers are never reused.

| Set | Covers | Blocks change |
| --- | --- | --- |
| §7.1 | what an invoice is, the scan window, document formats | 4 |
| §7.2 | what lands on the calendar | 7 |
| §7.3 | Google accounts (Q3.1–Q3.6) and the credentials removal (Q3.7–Q3.10) | 6, and **5** |
| ~~§7.4~~ | ~~Thunderbird accounts~~ — **fully resolved**, §3.8 | ~~3~~ **ready to specify** |
| §7.5 | housekeeping, except Q5.13 which #4's first migration needs | 4, for Q5.13 only (#1 **ready to specify**) |
| **§7.6** | **templates** | **2** |

### 7.1 What an invoice *is*, and how far back to look — blocking

- **Q1.5 — What happens to a message that yields no invoice?** Templates give this four distinct causes, not one: no supplier matched the sender, the supplier has no template, a template ran but a rule found nothing, or an attachment was unreadable. Skipped silently, listed with the reason, or a hard error for the whole scan?
  *Default:* listed with the specific reason, never silent — and never fatal to the scan. These four messages are how you find out which template needs fixing, so they are a feature of the templates workflow rather than noise. Worth a filter on the table for "problems only".
- **Q1.6 — Which date does the scan window measure?** This is the one that changes behaviour rather than wording, and there are three candidates:
  - **the date the mail arrived** — sits in the message header, so the reader can skip a message without parsing it, which is what makes a 7-day window fast;
  - **the invoice issue date** — inside the document, so every message in the mbox must be fully parsed before it can be excluded, and the window stops being an optimisation;
  - **the due date** — same cost, and it points *forwards*, so "90 days" would mean something different again.

  They disagree in practice: an invoice that arrived 150 days ago but falls due next week is inside a 180-day window and outside a 30-day one on the first reading, and the reverse on the third.
  *Default:* the date the mail arrived, with the picker labelled so it says that out loud ("mail received in the last 90 days") rather than leaving you to guess.
- **Q1.9 — Does changing the window rescan immediately, or after a Refresh click?** Immediate is nicer until a 180-day scan over a large mbox takes noticeable seconds, at which point every click through the picker costs one.
  *Default:* immediate — but §3.8 changed my confidence here. With 6.2 GB in scope, "immediate" is only tenable on the back of the incremental scanning in Q4.10, and it may still want an explicit Refresh. Treat this as provisional until change #4 measures a real scan.

New, opened by the answers in §1.1:

- **Q1.10 — What happens to an invoice with no due date?** Q1.2 makes due date a field, but a text-body invoice may simply not state one, and without it there is no event to create. Reject it as unparsed, or store it and list it as "not uploadable"?
  *Default:* store and list it, greyed out with a reason. §3.2's `UploadableInvoice` type makes the distinction impossible to get wrong in code, and you keep the record of an invoice that exists whether or not it can go on a calendar.
- **Q1.11 — How does a reader receive an attachment?** It lives inside an mbox, so there is no file path. Either the domain's document dependency takes **bytes** (`DocumentSource`, §3.2) or the Thunderbird adapter spills each attachment to a temp file to reuse the existing path-based `ReadDocumentContent`. Bytes also means deciding what happens to the existing type and its contract suite (§5.7).
  *Default:* bytes, with a new `ReadDocumentText` returning text lines; leave `ReadDocumentContent` and its coordinate-bearing `Word`s in place for anything that genuinely needs layout. Temp files for every attachment on every scan is a cleanup problem for no benefit.
- **Q1.12 — "DOC" — do you mean `.docx`, or genuinely legacy `.doc`?** `.docx` is a day's work with DocumentFormat.OpenXml. Pre-2007 binary `.doc` needs NPOI and is several days of unpleasantness for a format most suppliers stopped emitting fifteen years ago.
  *Default:* `.docx` only, with a clear "unsupported format" row for legacy `.doc` so you can see whether it ever actually shows up.
- **Q1.13 — Email bodies: plain text only, or HTML too?** Most commercial invoice mail is HTML, which needs markup stripped to text before a template can run — and stripping changes line structure, which is exactly what label-based rules depend on.
  *Default:* both, preferring the `text/plain` alternative when the message carries one, and stripping HTML only when it doesn't. That keeps the text a template sees as close to what the sender actually wrote as possible.
- **Q1.14 — Should `MyDogsbody.Integrations.Pdf` be absorbed** into a single `MyDogsbody.Integrations.Documents` (rename, move `PdfDocumentReader.fs`, keep its tests), or should the new readers sit in a separate project beside it?
  *Default:* absorb. One project per capability rather than per library means the composition root binds one dependency type in one place.
- **Q1.15 — Roughly how many invoices are we talking about** — dozens, hundreds, thousands? It doesn't change the design, but it decides whether the invoice table needs paging, and whether a full rescan is a "press it whenever" action or a "go and make tea" one.
  *Default:* assume hundreds; server-side paging only if it turns out to be needed.

Opened by making the window list editable:

- **Q1.16 — What is the largest window a user may add?** Making the set editable removed the ceiling that "12 months" used to provide, so `ScanWindowDays.create` needs one. This is a guard against typing 1400 as 14000, not a statement about how far back you should look — but it is also the only thing standing between a slip and a scan of the whole 16 GB store.
  *Default:* 3650 days (ten years), with a minimum of 1. Big enough never to obstruct a real intention, small enough to catch a fat finger. If you want the guard to mean something stronger — 730 days, say, so a scan can never be more than a couple of years of mail — that is a one-line change and a better answer, but it is a policy decision rather than a safety one.
- **Q1.17 — What can be done to the list, and what happens to a window you're using?** Add is a given. Beyond that: may the five seeded values be deleted, or are they fixed? And if you delete the window you last selected, what does the invoices page open on next time?
  *Default:* add and delete, with no edit (a window is one number — changing it is a delete and an add) and no distinction between seeded and user-added rows, because a seeded value you never use is exactly the kind of clutter this page exists to let you remove. Deleting the last remaining window is refused. Deleting the one you had selected falls back to 14, or to the shortest window still present if 14 itself is gone — which is `ResolveScanWindowWorkflow`'s whole job and the reason the remembered choice is stored as a number rather than a foreign key.
- **Q1.18 — Is the cutoff measured from the start of today, or from the exact moment you clicked?** They differ by up to a day. Start-of-day means "the last 14 days" names a set of dates and stays stable until midnight; exact-instant means it quietly means "the last 336 hours" and the same window covers different mail at 09:00 and at 17:00.
  *Default:* start of day (`getCurrentTime().Date`). It is what a person means by "the last 14 days", it makes a rescan within one day genuinely idempotent, and it plays better with the per-folder watermarks in Q4.10. The cost is that a message that arrived this morning at 08:00 is inside a 1-day window all day — which is the behaviour you'd want anyway.

### 7.2 What lands on the calendar — blocking

- **Q2.5 — Over what time window does the diff look?** `Events.list` needs a bound, and now there are two windows in play — the mail scan window from Q1.6 and this one. Letting them drift apart produces nonsense: query 180 days of calendar against a 7-day scan and every older event reads as "on the calendar but missing from the source".
  *Default:* derive this one from the invoices actually found — the range they span, padded a month either side — so the scan window drives both and they cannot disagree. The diff then only ever answers "of what I scanned, what is already up there?".
- **Q2.6 — Insert-only, or also update and delete?** If an invoice's amount changes after upload, does the existing event get updated? If the user deletes the event by hand, does the next upload put it back?
  *Default:* insert-only for change #7; deletion by hand means it comes back on the next upload. Say so on screen.
- **Q2.7 — Upload everything, or a selection?** Checkboxes per row, or one "upload all missing" button?
  *Default:* one button uploading everything currently missing, with the count shown on it.
- **Q2.8 — Partial failure mid-batch?** Stop at the first failure, or continue and report "14 of 17 uploaded, 3 failed"?
  *Default:* continue, then report per-row outcomes.
- **Q2.9 — Does the scan window bound the import, the view, or both?** Now that invoices persist (§1.1), these come apart. The window necessarily bounds the **import** — it's what decides which mail is read. Whether it also filters the **table** is a separate choice: the store may well hold invoices from outside it.
  *Default:* both, with the window and count stated above the table ("41 invoices, received in the last 90 days") so the list is obviously a view of a range rather than the whole ledger. Nothing is deleted when you narrow it — narrowing hides, it does not forget — and the upload button acts only on what is visible.

Opened by the answers to Q2.3 and Q2.4:

- **Q2.10 — What value goes inside the private extended property?** Q2.4 settled the *mechanism*; this is the string it carries. It has to be stable across rescans and unique per invoice — which argues for the Q5.8 natural key (supplier + invoice reference) rather than the local database id, since that id would change if you ever rebuilt the ledger from scratch and every event would then read as missing.
  *Default:* a composite of supplier id and invoice reference under a single well-known property name, with the local invoice id stored alongside for diagnostics only.
- **Q2.11 — What if an account's default invoice calendar is unset, or has since been deleted at Google?** Q2.3 makes the calendar a property of the account, so both states are reachable — a freshly authorised account has no default until you choose one.
  *Default:* the invoices page shows that account as not ready, with a link to the Google accounts page, and disables the upload button rather than failing at the API. A calendar that has vanished at Google surfaces as `CalendarNoLongerExists`, not a raw 404.
- **Q2.12 — Can the calendar be overridden for a single upload?** Q2.3 sets a default per account; this is whether the invoices page may deviate from it.
  *Default:* no. One less control on that page, and one less way to file a month of invoices somewhere you didn't mean to. Changing it is a deliberate trip to the Google accounts page.

### 7.3 Google accounts — blocking

- **Q3.1 — How does consent happen?** `GoogleWebAuthorizationBroker` (as in the prototype) opens the *system* browser and listens on a loopback port. Acceptable inside this WPF/BlazorWebView app, or should the consent page render inside the WebView?
  *Default:* system browser + loopback. It's what the prototype already does and it's the flow Google recommends for desktop apps.
- **Q3.2 — Where does the OAuth client secret (`credentials.json`) come from?** Shipped with the app, pasted by you once, or one per account?
  *Default:* one client secret for the app, pasted once and stored, reused by every account.
- **Q3.5 — How is an account labelled in the table?** Showing the email address needs the extra `userinfo.email` scope at consent time. Acceptable, or should accounts get a nickname you type?
  *Default:* request `userinfo.email` and show the address — a nickname on top is fine, but a wrong address is confusing.
- **Q3.6 — Removing an account:** delete the stored token only, or also revoke it at Google?
  *Default:* delete locally; mention that revoking happens in the Google account settings.

Opened by removing the credentials integration (§3.9) — these block change #5, not #6:

- **Q3.7 — What happens to `MyDogsbody.Domain/Credentials/`?** It holds `CredentialsTypes.fs` and three workflows, all currently green. Does a generic credentials area survive with the provider as a parameter, does each provider get its own credential types in its own domain area, or does the concept stop being a domain concern at all once a credential is just a token the Google adapter needs?
  *Default:* it stops being a domain area. Nothing in the domain reasons about a credential — no workflow makes a decision from one — so it is infrastructure the Google adapter holds, not a modelled concept. That deletes three workflows and their tests, which is a real loss of coverage to state plainly in the change description.
- **Q3.8 — Do `MyDogsbody.Enums.InfrastructureType` and the domain's `Infrastructure` union go too?** If the database identifies the provider (§3.9), the discriminator is redundant. `MyDogsbody.Enums` is a whole C# project that exists only to share that one enum.
  *Default:* both go, and `MyDogsbody.Enums` with them. Fewer projects, one less pair of edge mappers, and one less way for the two spellings to disagree. Worth confirming nothing else is planned for that enum.
- **Q3.9 — What happens to the rows already in `Credentials.db`?** Migrate them into the provider databases, or discard and re-enter?
  *Default:* discard. It is a development database in `bin\Debug\net9.0\`, and writing a one-shot migration for a handful of rows you can retype costs more than it saves. Say so explicitly rather than letting the file quietly stop being read.
- **Q3.10 — Does `/settings/credentials` disappear entirely?** Once each provider owns its credentials, the Google accounts page *is* Google's credential page. A generic page listing everything would have to reach into every provider's database, which is exactly the coupling §3.9 removes.
  *Default:* it goes, along with `CredentialsComponents`, `CredentialsBrowserModule` and their tests. The nav entry is replaced by the per-provider pages.

### 7.4 Thunderbird accounts — ✅ fully resolved

Nothing open. All eleven questions are answered, the plan is measured against the real profile in §3.8, and **change #3 can be specified as soon as you want it.** Alongside change #1 it is one of the two pieces of this build that is ready to write requirements for today.

### 7.5 Storage and process — decide during design

- **Q5.4 — Does the seven-change breakdown in §4 suit you?** It grew from four as a direct result of the §1.1 answers. If it feels like too much ceremony, the honest minimum is four: foundation+templates, extraction, the credentials refactor, sync.
  *Default:* seven, in the order given, starting with `invoice-ledger-foundation`.
- **Q5.6 — Encryption at rest for OAuth tokens (§5.5)** — in scope now, or explicitly deferred?
  *Default:* deferred, and noted in the change description so it isn't forgotten.
- **Q5.10 — Do templates need export/import?** They will represent real effort to get right, and they'd currently live only in a SQLite file in `bin\Debug\net9.0\`. A JSON export would make them backup-able and reproducible after a clean rebuild.
  *Default:* not in the first pass, but keep the template model serialisable so adding it later is a page, not a redesign. Worth saying out loud that a `dotnet clean` should never be able to destroy a morning's template work.
- **Q5.11 — How much provenance does an invoice keep, now that its source is an Integration?** `SourceMessageId` is already on the invoice — a Thunderbird-shaped fact sitting on a MyDogsbody item. Should the invoice also record the account and folder it came from, which would help answer "why did this appear?", or is that leaking integration vocabulary into the ledger?
  *Default:* keep `SourceMessageId`, add nothing else. A message id is a standard identifier that still means something outside Thunderbird; account and folder names are that client's vocabulary and stop making sense the moment mail is moved or the client changes. If a diagnostic view wants more, the Thunderbird integration can answer from the message id.
- **Q5.12 — Can an invoice be corrected or deleted by hand?** Opened by Q5.7: now that the ledger is real, a mis-parsed amount is a stored wrong number rather than a transient display glitch. Is the only recourse to fix the template and rescan, or can you edit the row? And can you delete an invoice that was never really one?
  *Default:* delete yes, edit no — with a caveat. Deleting is needed because a bad template will produce junk rows before you get it right. Editing is the trap: a hand-corrected value would be silently overwritten by the next rescan under the Q5.8 key, which is worse than not offering it. If editing is wanted, it needs an "edited by hand, don't overwrite" flag on the row, and that is a real design addition rather than a checkbox.
- **Q5.13 — Is the settings table typed, or key/value?** Opened by making the scan window a remembered choice: it is the app's first stored preference, so whatever shape it takes is the shape every later setting copies. A single-row `InvoiceSettings` table with a typed column per setting means one migration per setting and a store that says what it holds; a generic `Settings(Key, Value)` table means one migration ever, and every setting arriving as a string that some caller has to parse and validate at the point of use.
  *Default:* typed. Migration-per-setting is already the rule for every other schema change here, and stringly-typed storage is what the rest of this codebase spends real effort avoiding — a scan window that comes back as `"fourteen"` should not be reachable. The honest counterargument is that a handful of scalar preferences is exactly the case key/value was invented for; if you expect a dozen of them, say so now rather than after six migrations.

### 7.6 Templates — the new question set, blocking for change #2

This is the part with no precedent in the codebase, and the answers decide how big change #2 is.

- **Q7.6.1 — Which rule kinds does the first version need?** §3.7 proposes four: `AfterLabel`, `LinesAfterLabel`, `RegexCapture`, `FixedValue`. Every kind is an editor to build and a concept to learn, so fewer is genuinely better if it covers your suppliers.
  *Default:* all four. `AfterLabel` will do most of the work, `RegexCapture` is the escape hatch for when it doesn't.
- **Q7.6.2 — Do you want raw regex on the page at all?** It is the most powerful option and the one most likely to hang a scan or fail silently against a layout change. Labels only would be safer, simpler and less capable.
  *Default:* keep regex, with the mandatory match timeout from §3.7. You're the only user, and the escape hatch is worth more than the protection.
- **Q7.6.3 — A supplier has several templates. How is the right one chosen?** By document part (body vs PDF), by explicit priority order, or "try each until one yields every required field"?
  *Default:* filter by document part, then try in the user's order, first complete match wins — and record which template produced each invoice so a wrong answer is diagnosable.
- **Q7.6.4 — Two suppliers match the same message. Then what?** An error on that message, or first-match-wins by an order you control?
  *Default:* treat it as an error against that message and show it in the table. Silently picking one is how you get a month of invoices filed under the wrong supplier.
- **Q7.6.5 — How is a supplier matched?** Sender address, sender domain, or subject pattern — and may a supplier hold several matchers?
  *Default:* all three kinds, several per supplier, matching on any. Sender domain will be the one you actually use.
- **Q7.6.6 — Is the "test against a message" panel in scope for change #2?** §3.7 argues it is close to essential — editing extraction rules against text you cannot see is guesswork. It is also perhaps a third of the page's work.
  *Default:* yes, in scope. It's the difference between a feature you can use and one you fight.
- **Q7.6.7 — When a template changes, what happens to invoices it already produced?** Leave them, re-extract them on the next scan, or offer a "reprocess this supplier" button?
  *Default:* leave stored invoices alone; the next scan of a window covering them updates them via the Q5.8 key. A reprocess button is a good follow-up, not a first-pass necessity.
- **Q7.6.8 — Currency: per supplier, or extracted per invoice?** A supplier almost always bills in one currency, and extracting it is one more rule to get wrong.
  *Default:* a fixed value on the template (`FixedValue`), overridable by a rule if a supplier ever needs it.

---

## 8. Next step

§1.1 has now closed thirty questions and opened thirty-eight — 40 open before the scan-window answer, 42 after it. That is normal for a pre-proposal, and it is exactly why this file exists rather than a `requirements.md`: most of those would otherwise have surfaced mid-implementation, when they cost more.

**The scan-window answer is the clearest example of that so far.** "7, 14, 30, 90, 180 and let me add more" reads like a change to a list of numbers. It is not: making the set editable turns a closed union into a table, deletes a compile-time guarantee and replaces it with a validation boundary, adds a settings page and two migrations, introduces the app's first stored preference and therefore its first settings table, and forces a decision about what "the last 14 days" measures from. Four new questions, one reversed UI recommendation, and one design argument in §3.5 that had to be struck out rather than quietly edited. All of it cheap here, none of it cheap once `ScanWindow` is a union in a shipped domain.

**§3.8 is the part of this document I trust most**, because it is the only part measured rather than reasoned. Two of my defaults were wrong — structural account detection and copy-then-parse — and both would have survived into code. It is worth doing the equivalent for the templates in §3.7 before change #2: two real invoices from two real suppliers, checked against the four rule kinds.

**Two changes are ready now, with nothing left to ask of either:**

- **Change #1, `invoice-ledger-foundation`.** Q5.7, Q5.8 and Q5.9 were the last blockers and all three are settled. It is small, has no external dependency, and it proves the main SQLite database, its migrations and its store shape — which every later change leans on. **Start here.**
- **Change #3, `thunderbird-account-selection`.** §7.4 is empty and §3.8 is measured against your actual profile. It is also the change that produces something you can look at — a page listing your ten real accounts — without touching Google, SQLite or templates.

They are independent, so either order works, or both.

**One more a question away:** Q3.7–Q3.10 → **change #5**, the credentials refactor. It depends on nothing and blocks the Google work, and it is the only change here that makes the codebase *smaller* — a good one to have behind you.

**§7.6 is the one to think hardest about.** It decides change #2, which is the largest and least precedented piece of the build, and no amount of design work elsewhere will de-risk it. If you want to sanity-check the template model before committing to it, the fastest test is to take two real invoices from two different suppliers and check whether the four rule kinds in §3.7 can actually locate every field.

The rest stay blocking only for the change that needs them: §7.1 → #4, §7.2 → #7, §7.3 → #6. §7.5 is housekeeping apart from **Q5.13**, which change #4 needs before it writes its first migration — a settings table is easy to add and awkward to reshape once something is stored in it.

**One question worth answering early even though it blocks nothing yet: Q4.8.** Which folders get scanned decides whether change #4 reads 6.2 GB or 15.2 GB, and that single choice has more effect on how the app feels than anything else in this proposal.

Requirements in EARS notation, agreed before any `design.md`, per CLAUDE.md.
