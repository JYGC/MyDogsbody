# Pre-proposal — Invoices from Thunderbird to Google Calendar

**Status:** pre-proposal. Not a change folder yet — no `requirements.md` exists, and none should be written until the blocking questions in §7 are answered.
**Date:** 2026-08-08

---

## 1. What was asked for

Six user-facing pieces:

1. **An invoice page** with a table that lists every invoice found in one account of the Thunderbird profile folder, and a button that uploads them to a chosen Google account's calendar.
2. **A scan window on that page** — a chooser for how far back to scan the invoice source: **1, 2, 3, 4, 6 or 12 months from today**. The table shows what falls inside the chosen window, and changing it rescans.
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
| **Q3.3 / Q3.4** — where credentials live | **Remove the separate credentials integration.** Credentials go in a `Credentials` collection inside **the provider integration's own LiteDB** | The only answer here that deletes existing, working, tested code. It needs a change of its own — §3.8 |
| **Q5.1** — which storage tier | **Invoices are MyDogsbody items, not Integration items.** So are **suppliers** | The main SQLite database stops being theoretical: suppliers, templates and invoices all persist there, behind FluentMigrator migrations. §3.6 |

#### What "MyDogsbody items, not Integration items" is taken to mean

Invoices and suppliers are the application's own concepts with their own lifecycles; the integrations are only where they came from and where they are pushed to. Concretely:

- The types live in `MyDogsbody.Domain/Invoices/` and `MyDogsbody.Domain/Suppliers/` and name nothing from Thunderbird, Google, mbox or MIME — already true in §3.2, and now the reason it must stay true.
- **Supplier is an entity, not a string field.** An invoice carries a `SupplierId` and the supplier record carries the name — which is what makes "every invoice from this supplier" answerable, and what a template hangs off. A free-text supplier name on each invoice would give you three spellings of the same company and no way to attach a template to any of them.
- **Suppliers, templates and invoices persist in the main SQLite database.** CLAUDE-project.md reserves the per-integration LiteDB stores for *an integration's own* data, and none of these are that. So this feature becomes the **first consumer of `MyDogsbody.Database`** — designed, migrated, never wired in — and adds the first real migrations alongside the `Blog`/`Comment` scaffold. Taken together the three tables are a small ledger, which is a fair description of what this app is becoming.
- `Integrations.Thunderbird` owns only Thunderbird's own facts: the profile path, which account is selected. It hands over messages and attachments and does not store, define or number invoices.
- `Integrations.Google` owns only Google's own facts: registered accounts, their default invoice calendar, and — per §3.8 — their **credentials**, in a `Credentials` collection in its own database.
- **An invoice outlives both.** Removing the Google account, or switching the Thunderbird account, does not delete invoices or suppliers.
- Reading a PDF is still an *integration* — it's an adapter for a capability the domain declares. Don't read this as moving `PdfDocumentReader` inward; the reading is infrastructure, the invoice is not.

The one place this may overshoot is **persistence**. If you meant only "the concept belongs to the domain", and you're happy for the table to be a live view of your mailbox recomputed on each scan and stored nowhere, say so — it is a materially smaller build. Note that templates push hard the other way: a template you typed has to be saved somewhere regardless, so at least part of the ledger is now unavoidable. See **Q5.7**.

---

## 2. What already exists to build on

| Piece | Where | How it helps |
| --- | --- | --- |
| Credential store (LiteDB) | `MyDogsbody.Integrations.Credentials` | **Being removed** — §1.1 moves credentials into each provider's own database. Its store/mapper/context shape is still exactly what a provider's `Credentials` collection should copy. §3.8 |
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
Thunderbird profile ──┐  Integrations.Thunderbird
  profiles.ini        ├──►  ListMailAccounts
  prefs.js accounts   ├──►  ReadMailFolder account cutoff ──► messages + attachments
  mbox / maildir      ┘                                              │
                                                                     ▼
PDF / DOC / TXT ──────┐  Integrations.Documents                      │
  PdfPig              ├──►  ReadDocumentText — one type, four        │
  OpenXml / NPOI      ├──►  readers, chosen by the format the   ─────┤
  plain text + body   ┘     message actually carries                 │
                                                                     ▼
  ScanWindow (page) ─┐                              Domain/Invoices  ExtractInvoicesWorkflow
  GetCurrentTime ────┴────────────────────────────────────────────►    │  cutoff = now - window
                                                                       │  match supplier
main SQLite ──► LoadSuppliers, LoadTemplates ─────────────────────►    │  apply template (pure)
(MyDogsbody.Database)                                                  │
            ◄── SaveInvoices ◄─────────────────────────────────────────┤  StoredInvoice list
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
- Error DU: `InvoiceError` — `InvoiceReferenceInvalid`, `AmountUnparseable`, `MailStoreUnreadable of message`, `NoAccountSelected`, `SupplierNotRecognised of sender`, `NoTemplateForSupplier of SupplierId`, `TemplateMatchedNothing of fieldName`, `AttachmentUnreadable of filename * reason`, …
- Dependency function types: `ListMailAccounts`, `LoadSelectedMailAccount`, `SaveSelectedMailAccount`, and
  - `ReadMailFolder = MailAccountId -> ScanCutoff -> Result<MailMessage list, InvoiceError>` — the cutoff is a parameter so the adapter stops reading rather than reading everything and discarding. On a 12-month window over a large mbox that difference is the whole responsiveness of the page.
  - **`GetCurrentTime = unit -> DateTime`** — new, and required by the rules: CLAUDE.md forbids the domain reading a clock, and "months from present" needs one. Production binds `fun () -> DateTime.Now` at the composition root; every test binds a fixed instant, which is what makes the cutoff arithmetic assertable at all.
  - **`ReadDocumentText = DocumentSource -> Result<TextLine list, InvoiceError>`** — one type for all four formats, where

    ```fsharp
    type DocumentFormat = Pdf | Word | PlainText | EmailBody

    /// Bytes, not a path: an attachment lives inside an mbox, and it should not have to be
    /// spilled to a temp file (and cleaned up, and kept out of a backup) to be read.
    type DocumentSource = { Format: DocumentFormat; Name: string; Content: byte[] }
    ```

    The composition root binds one reader per format and dispatches on `Format`, so the domain sees a single function and adding a fifth format never touches a workflow. **This does not match the existing `ReadDocumentContent`**, which takes a `DocumentPath` and returns coordinate-bearing `Word`s — see Q1.11, it is a real decision, not a detail.
  - **The store, as functions**: `LoadInvoices = ScanCutoff option -> Result<StoredInvoice list, InvoiceError>`, `UpsertInvoices = ValidInvoice list -> Result<StoredInvoice list, InvoiceError>`, `MarkSynced = InvoiceId -> CalendarEventId -> Result<unit, InvoiceError>`. Upsert rather than insert, because rescanning the same window must not double the ledger — see Q5.8 for what makes two scans agree they found the same invoice.

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
- `ScanForInvoicesWorkflow` — `getCurrentTime → readMailFolder → readDocumentText → loadSuppliers → loadTemplates → upsertInvoices → (account, ScanWindow) → StoredInvoice list`. The orchestration: work out the cutoff, pull the messages, flatten each to text, match a supplier, apply its template, validate, store. Note how much of it is calls to the two pure workflows above — that is the shape to aim for. The cutoff arithmetic (`now.AddMonths(-months)`) is a private pure function in this file, so "6 months back from 31 March" is a unit test with a fixed clock and no mail store anywhere near it.
- `DiffInvoicesAgainstCalendarWorkflow` — **pure**: `StoredInvoice list -> CalendarEvent list -> InvoiceSyncStatus list`. Still the heart of the feature and still the cheapest thing to test, provided the match key is a value and not a heuristic.
- `UploadInvoicesToCalendarWorkflow` — takes the diff result, calls `CreateCalendarEvent` for the missing ones only, and `MarkSynced` for each success.
- Supplier and template CRUD: `AddSupplierWorkflow`, `EditSupplierWorkflow`, `DeleteSupplierWorkflow`, `ListSuppliersWorkflow`, and the same four for templates. These are the same shape as the existing `AddCredentialWorkflow` / `EditCredentialWorkflow` and should be written by copying them.
- `ListMailAccountsWorkflow`, `SelectMailAccountWorkflow`, `RegisterGoogleAccountWorkflow`, `ListGoogleAccountsWorkflow`.

### 3.3 Outer ring

**`MyDogsbody.Integrations.Thunderbird`** (new) + `.Database.Models` (C#, if it stores anything)
- `ThunderbirdProfileReader.fs` — `profiles.ini` → profile paths.
- `ThunderbirdAccountReader.fs` — `prefs.js` → account list (`mail.account.*`, `mail.server.serverN.*`).
- `MailFolderReader.fs` — mbox and/or maildir → messages; MIME parsing (MimeKit is the realistic pick) for attachments. **Takes the cutoff and honours it while reading**: skip a message on its `Date`/received header before touching its body or attachments, so a 1-month window doesn't pay for a 12-month mbox. mbox is append-ordered in practice but not guaranteed sorted, so it still has to be walked end to end — the saving is in not parsing MIME or opening PDFs for messages outside the window, which is where the time actually goes.
- Its own small LiteDB store for the selected-account setting and the profile path override. Those are Thunderbird's own facts, so under §1.1 they stay here rather than going to the main database.
- **Hands over bytes, not paths.** An attachment is extracted from the message in memory and passed on as a `DocumentSource`; nothing is spilled to disk. Q1.11.

**`MyDogsbody.Integrations.Documents`** — the four readers behind `ReadDocumentText`
- `PdfDocumentReader.fs` — PdfPig. **Already exists**, in `MyDogsbody.Integrations.Pdf`, with a contract suite.
- `WordDocumentReader.fs` — `.docx` via DocumentFormat.OpenXml. Legacy `.doc` is a different and much worse problem — Q1.12.
- `PlainTextDocumentReader.fs` — decoding and line splitting; no library.
- `EmailBodyReader.fs` — the message body itself, which for HTML mail means stripping markup to text — Q1.13.

Whether this project *absorbs* `MyDogsbody.Integrations.Pdf` (rename, move the file, keep the tests) or sits beside it is Q1.14. Absorbing is my recommendation: one project per *capability* rather than per library keeps the composition root binding one dependency type in one place.

**`MyDogsbody.Integrations.Google`** (exists as a stub)
- `GoogleCalendarClient.fs` — `CalendarService` behind `ListCalendars` / `ListCalendarEvents` / `CreateCalendarEvent`, lifted from the `GoogleCalendarCRUD` prototype.
- `GoogleAccountStore.fs` — registered accounts, their default invoice calendar, and **their credentials**, all in `Google.db`: an `Accounts` collection and a `Credentials` collection in the provider's own database (§1.1, §3.8).
- `GoogleAuthorization.fs` — the consent flow (Q3.1).

Both follow the existing rules: `Result<'T, MyDogsbodyException>`, `handleError`, an `ActionNames` entry per function, the collection getter stopping at the integration boundary, and a `BsonMapper.Global.ToDocument` warm-up in any new LiteDB context.

### 3.4 Composition root

Five new API factories, each in the established three-file split with no module-level I/O outside `Startup.fs`:

- `SupplierApiFactory.fs` + `SupplierApiMappers.fs`
- `TemplateApiFactory.fs` + `TemplateApiMappers.fs`
- `MailAccountApiFactory.fs` + `MailAccountApiMappers.fs`
- `GoogleAccountApiFactory.fs` + `GoogleAccountApiMappers.fs`
- `InvoiceSyncApiFactory.fs` + `InvoiceSyncApiMappers.fs`

And **one pair deleted**: `CredentialApiFactory.fs` + `CredentialApiMappers.fs`, per §3.8.

`Startup.fs` gains the SQLite context, the provider LiteDB handles and the `GetCurrentTime` binding, loses the credentials context, and ends with five registrations instead of one. `MainWindow.xaml.cs` still does not change — that is the property worth preserving through all of this.

### 3.5 UI

**`MyDogsbody.UI.Types`** — new records (no domain types leak here):
- `GoogleAccountUiType`, `CalendarUiType`, `MailAccountUiType`, `SupplierUiType`, `TemplateUiType`, `FieldRuleUiType`, `InvoiceUiType` (with a `SyncStatus` field and a resolved supplier name — the UI shows a name, the domain holds an id), and the API records `GoogleAccountApi`, `MailAccountApi`, `SupplierApi`, `TemplateApi`, `InvoiceSyncApi`.
- **`ScanWindowUiType`** — the UI needs its own, because `UI.Types` cannot reference `MyDogsbody.Domain`. Either a six-case union mirroring the domain's, or a plain `int` of months. Recommend the **union**, mapped in `InvoiceSyncApiMappers` with an exhaustive match in both directions: an `int` puts `7` back on the table and moves the "is this a legal window?" check from the compiler into a runtime error. This is the same tax `Infrastructure` ⇄ `InfrastructureType` already pays, for the same reason.
- `Modules/GoogleAccountsBrowserModule.fs`, `Modules/MailAccountsBrowserModule.fs`, `Modules/InvoiceSyncModule.fs` — each a record of `aval` fields + commands, same shape as `CredentialsBrowserModule`. `InvoiceSyncModule` carries `SelectedScanWindowAval: aval<ScanWindowUiType>`, `AvailableScanWindowsAval` and `SelectScanWindow: ScanWindowUiType -> unit`; selecting one is a write-then-reload exactly like an edit, so the table always shows what was actually scanned rather than what the picker was holding.

**`MyDogsbody.UI.Portal`** — three pages, registered in `Shell.fs`, the two settings pages also linked from `SettingsComponents.settingsNavMenu`:

| Page | Route | Contents |
| --- | --- | --- |
| Invoices | `/invoices` (top-level, not settings) | **Scan-window picker (1 / 2 / 3 / 4 / 6 / 12 months)**, Google-account picker — **no calendar picker**, the account carries its own (Q2.3), shown read-only beside it — refresh, invoice table with a per-row sync-status column, "Upload to calendar" button, `MudAlert` from `ErrorAval` |
| Google accounts | `/settings/google-accounts` | Table of registered accounts, "Add account" → consent flow, **the default invoice calendar for each account, picked from a dropdown of that account's own calendars**, remove / re-authorise |
| Thunderbird accounts | `/settings/mail-accounts` | Table of accounts discovered in the profile, radio/select for the one to import from |
| **Suppliers** | `/settings/suppliers` | Table of suppliers with their match rules; add / edit / delete via dialog; each row opens that supplier's templates |
| **Templates** | `/settings/suppliers/{id}/templates` | The templates for one supplier: field rules, and — strongly recommended — a **"test against a message" panel** that runs `ApplyTemplateWorkflow` over a real scanned message and shows what each rule extracted. See §3.7 |

Both new settings pages are the credentials page again: `MudTable` + toolbar button + `FunComponent` dialog + module creator with `cval`/`transact`. Nothing novel, which is the point — the novelty is all in §3.7.

The scan-window picker is six fixed choices, so a `MudToggleGroup` of six buttons reads better than a dropdown — the whole range is visible and switching is one click, which matters if the intended use is "try 3, then look further back". A `MudSelect` is the fallback if the toolbar gets crowded. Either way it renders `AvailableScanWindowsAval`, never a hard-coded list in the component.

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

`Blog` and `Comment` stay as they are — they're scaffold, and nothing here disturbs them.

The last table is the one worth arguing about. A sync record is a fact about *an invoice*, so it belongs on this side rather than in the Google integration's store — but it also means the app has an opinion about what's on a calendar that can go stale if you delete an event by hand. **The calendar remains the source of truth for the diff** (Q2.4's extended-property query), and this table is history: what we uploaded, when, to which account. Don't let it become the answer to "is it there?".

**Where the store functions live** is a genuine choice — see Q5.9. Recommendation: `MyDogsbody.Database` gains `SupplierStore.fs`, `TemplateStore.fs`, `InvoiceStore.fs` and their record ⇄ domain mappers, plus a `ProjectReference` to `MyDogsbody.Domain`. It's outer-ring code either way, so it keeps the outer-ring shape: dependencies first, input last, `Result<'T, MyDogsbodyException>`, written with `handleError`, one `ActionNames` entry per function.

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

### 3.8 Credentials move into the provider integrations

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
| 3 | `thunderbird-account-selection` | Thunderbird accounts page, profile/account discovery, selection persisted. Ask #5 | — |
| 4 | `invoice-extraction` | The four document readers, the scan window, the `Invoices` migration, the scan pipeline, a read-only invoice table. Ask #2 | 1, 2, 3 |
| 5 | `credentials-per-provider` | **Pure refactor, no new feature.** Deletes `Integrations.Credentials`; each provider integration gains a `Credentials` collection in its own LiteDB. Characterization tests before anything moves. §3.8 | — |
| 6 | `google-account-integration` | Google accounts page, OAuth registration, calendar listing, **and the per-account default invoice calendar** — Q2.3 moved that here from #7. Ask #4 | 5 |
| 7 | `invoice-calendar-sync` | The diff, the sync-status column, the upload button, `InvoiceCalendarEvents`. Asks #1 and #3 | 4, 6 |

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
12. **The clock is a new dependency function type, and CLAUDE.md calls those published interfaces** — meaning `GetCurrentTime` owes a contract suite run against the real implementation *and* every fake. The real implementation is `DateTime.Now`, whose whole nature is to return something different each call, so "assert both sides agree" has no obvious meaning. Workable answer: the suite asserts the properties that must hold of any clock (monotonic across two calls, `Kind` as expected, within a tolerance of `DateTime.Now` at the point of test) and the *cutoff arithmetic* — the part with actual logic — is unit-tested against fixed instants in the workflow. Say this explicitly in the change rather than quietly having no contract test for the clock. Worth checking whether month arithmetic near month ends behaves as you'd want: `.AddMonths(-1)` from 31 March gives 28 February, not 3 March.

---

## 6. What would be true when it's done

- `dotnet build MyDogsbody.sln` clean; `dotnet test` green with zero skips, across all four levels — with the one honest exception in §5.2 declared in the change description.
- `MyDogsbody.Domain` still has zero `ProjectReference` elements (`AssertDomainReferencesNothing` + `Contracts/DomainIsolationTests.fs` both still pass).
- Still exactly two mapping points per feature: entity ⇄ domain in each integration, domain ⇄ UI record in `Startup/*ApiMappers.fs`.
- `UI.Portal` still references only `UI.Types`, `Enums`, `Exceptions.Types`.
- Uploading twice in a row creates no duplicate events.
- The six scan windows exist in exactly two places — the domain union and its UI mirror — with an exhaustive match between them, so a seventh could never be half-added. No component holds its own list of months.
- No workflow reads the clock directly; every cutoff test pins a fixed instant and asserts an exact date.
- **Adding a supplier and teaching the app to read its invoices is done entirely on screen** — no rebuild, no F# change, no restart. That is the whole point of ask #6, and it is the acceptance test for it.
- Suppliers, templates and invoices live in the main SQLite database, with the schema built **only** by FluentMigrator — no DDL in a store function, a test, or a SQLite tool.
- A template carrying a pathological regex fails *that rule* with a named error inside its timeout, and the scan finishes. It does not hang the page.
- `MyDogsbody.Domain` still names no Thunderbird, Google, LiteDB, SQLite, MIME or PDF type — the ledger got bigger, the centre did not get less pure.
- **No `Credentials.db` and no `MyDogsbody.Integrations.Credentials`.** Each provider's credentials sit in that provider's own database, and nothing outside a provider integration opens it. The solution has one fewer project than it does today, possibly two once `MyDogsbody.Enums` goes (Q3.8).

---

## 7. Questions to answer

Answer the **blocking** ones before the `requirements.md` for the change they block. The rest can be decided during design, but earlier is cheaper. Each carries my recommendation — "default" is what I'd write if you just said "use your judgement".

**Answered questions are removed from this section** — the decisions they became are recorded in §1.1 and built into §3. What is left below is only what is still open: **48 questions**, of which §7.6 and Q5.7 are the two sets standing between here and a first `requirements.md`. Numbering is not contiguous; gaps are answered questions, and numbers are never reused.

| Set | Covers | Blocks change |
| --- | --- | --- |
| §7.1 | what an invoice is, the scan window, document formats | 4 |
| §7.2 | what lands on the calendar | 7 |
| §7.3 | Google accounts (Q3.1–Q3.6) and the credentials removal (Q3.7–Q3.10) | 6, and **5** |
| §7.4 | Thunderbird accounts | 3 |
| §7.5 | storage, testing, process | 1 (via Q5.7, Q5.9) |
| **§7.6** | **templates — the new set** | **2** |

### 7.1 What an invoice *is*, and how far back to look — blocking

- **Q1.5 — What happens to a message that yields no invoice?** Templates give this four distinct causes, not one: no supplier matched the sender, the supplier has no template, a template ran but a rule found nothing, or an attachment was unreadable. Skipped silently, listed with the reason, or a hard error for the whole scan?
  *Default:* listed with the specific reason, never silent — and never fatal to the scan. These four messages are how you find out which template needs fixing, so they are a feature of the templates workflow rather than noise. Worth a filter on the table for "problems only".
- **Q1.6 — Which date does the scan window measure?** This is the one that changes behaviour rather than wording, and there are three candidates:
  - **the date the mail arrived** — sits in the message header, so the reader can skip a message without parsing it, which is what makes a 1-month window fast;
  - **the invoice issue date** — inside the document, so every message in the mbox must be fully parsed before it can be excluded, and the window stops being an optimisation;
  - **the due date** — same cost, and it points *forwards*, so "3 months" would mean something different again.

  They disagree in practice: an invoice that arrived five months ago but falls due next week is inside a 12-month window and outside a 3-month one on the first reading, and the reverse on the third.
  *Default:* the date the mail arrived, with the picker labelled so it says that out loud ("mail received in the last 3 months") rather than leaving you to guess.
- **Q1.7 — Which window is selected on first open, and is the choice remembered** between runs? Q5.1 puts the selected mail account in the Thunderbird integration's own store; the window is arguably the same kind of setting.
  *Default:* 3 months, remembered alongside the selected account.
- **Q1.8 — Is 12 months the ceiling?** You named six values topping out at a year — confirming there's no "everything" option keeps the picker honest and stops a first run from walking a decade of mail.
  *Default:* 12 months is the maximum; no all-time option.
- **Q1.9 — Does changing the window rescan immediately, or after a Refresh click?** Immediate is nicer until a 12-month scan over a large mbox takes noticeable seconds, at which point every click through the picker costs one.
  *Default:* immediate, with the table showing the existing loading state. If change #4 shows real scans are slow, add caching then rather than a Refresh button.

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

### 7.2 What lands on the calendar — blocking

- **Q2.5 — Over what time window does the diff look?** `Events.list` needs a bound, and now there are two windows in play — the mail scan window from Q1.6 and this one. Letting them drift apart produces nonsense: query 12 months of calendar against a 1-month scan and every older event reads as "on the calendar but missing from the source".
  *Default:* derive this one from the invoices actually found — the range they span, padded a month either side — so the scan window drives both and they cannot disagree. The diff then only ever answers "of what I scanned, what is already up there?".
- **Q2.6 — Insert-only, or also update and delete?** If an invoice's amount changes after upload, does the existing event get updated? If the user deletes the event by hand, does the next upload put it back?
  *Default:* insert-only for change #7; deletion by hand means it comes back on the next upload. Say so on screen.
- **Q2.7 — Upload everything, or a selection?** Checkboxes per row, or one "upload all missing" button?
  *Default:* one button uploading everything currently missing, with the count shown on it.
- **Q2.8 — Partial failure mid-batch?** Stop at the first failure, or continue and report "14 of 17 uploaded, 3 failed"?
  *Default:* continue, then report per-row outcomes.
- **Q2.9 — Does the scan window bound the import, the view, or both?** Now that invoices persist (§1.1), these come apart. The window necessarily bounds the **import** — it's what decides which mail is read. Whether it also filters the **table** is a separate choice: the store may well hold invoices from outside it.
  *Default:* both, with the window and count stated above the table ("41 invoices, received in the last 3 months") so the list is obviously a view of a range rather than the whole ledger. Nothing is deleted when you narrow it — narrowing hides, it does not forget — and the upload button acts only on what is visible.

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

Opened by removing the credentials integration (§3.8) — these block change #5, not #6:

- **Q3.7 — What happens to `MyDogsbody.Domain/Credentials/`?** It holds `CredentialsTypes.fs` and three workflows, all currently green. Does a generic credentials area survive with the provider as a parameter, does each provider get its own credential types in its own domain area, or does the concept stop being a domain concern at all once a credential is just a token the Google adapter needs?
  *Default:* it stops being a domain area. Nothing in the domain reasons about a credential — no workflow makes a decision from one — so it is infrastructure the Google adapter holds, not a modelled concept. That deletes three workflows and their tests, which is a real loss of coverage to state plainly in the change description.
- **Q3.8 — Do `MyDogsbody.Enums.InfrastructureType` and the domain's `Infrastructure` union go too?** If the database identifies the provider (§3.8), the discriminator is redundant. `MyDogsbody.Enums` is a whole C# project that exists only to share that one enum.
  *Default:* both go, and `MyDogsbody.Enums` with them. Fewer projects, one less pair of edge mappers, and one less way for the two spellings to disagree. Worth confirming nothing else is planned for that enum.
- **Q3.9 — What happens to the rows already in `Credentials.db`?** Migrate them into the provider databases, or discard and re-enter?
  *Default:* discard. It is a development database in `bin\Debug\net9.0\`, and writing a one-shot migration for a handful of rows you can retype costs more than it saves. Say so explicitly rather than letting the file quietly stop being read.
- **Q3.10 — Does `/settings/credentials` disappear entirely?** Once each provider owns its credentials, the Google accounts page *is* Google's credential page. A generic page listing everything would have to reach into every provider's database, which is exactly the coupling §3.8 removes.
  *Default:* it goes, along with `CredentialsComponents`, `CredentialsBrowserModule` and their tests. The nav entry is replaced by the per-provider pages.

### 7.4 Thunderbird accounts — blocking

- **Q4.1 — "Accounts" means what exactly?** The mail accounts configured in Thunderbird (one per server/identity), or folders within one account? And is *one* selected at a time, or several?
  *Default:* Thunderbird accounts, exactly one selected at a time.
- **Q4.2 — How is the profile folder located?** Discovered from `%APPDATA%\Thunderbird\profiles.ini`, or a folder path you set in settings?
  *Default:* discover from `profiles.ini`, with an override field for when that's wrong.
- **Q4.3 — Which storage format do your accounts use?** mbox (the default) or maildir? IMAP accounts store cached copies under `ImapMail/`; POP/Local under `Mail/`. If you can say which of yours matter, change #3 can support just those first.
  *Default:* mbox first, maildir behind a follow-up change.
- **Q4.4 — Is Thunderbird likely to be running at the same time?** Determines whether the reader copies the store to a temp file before parsing.
  *Default:* assume yes; copy before parsing.

### 7.5 Storage and process — decide during design

- **Q5.2 — Do you accept the contract-test compromise in §5.2** (fakes + stubbed HTTP, with live Google verified manually and recorded as such)?
  *Default:* yes.
- **Q5.3 — Test fixtures:** may sample Thunderbird mbox files and sample invoice PDFs be committed to the repo for tests? They'd need anonymising.
  *Default:* yes, hand-built synthetic ones rather than real mail.
- **Q5.4 — Does the seven-change breakdown in §4 suit you?** It grew from four as a direct result of the §1.1 answers. If it feels like too much ceremony, the honest minimum is four: foundation+templates, extraction, the credentials refactor, sync.
  *Default:* seven, in the order given, starting with `invoice-ledger-foundation`.
- **Q5.5 — Should `GoogleCalendarCRUD` be deleted** once `Integrations.Google` does the same work for real?
  *Default:* delete it in change #6, the way `Spine` was deleted when it was superseded.
- **Q5.6 — Encryption at rest for OAuth tokens (§5.5)** — in scope now, or explicitly deferred?
  *Default:* deferred, and noted in the change description so it isn't forgotten.
- **Q5.7 — Confirming the inference: do invoices actually persist?** §1.1 reads "invoices are MyDogsbody items" as *stored* — a ledger you accumulate, surviving a switch of mail account. The alternative reading is that the concept merely belongs to the domain, and the table is a live view of your mailbox recomputed on each scan and stored nowhere. The second is a materially smaller build; it also means an invoice you already uploaded vanishes from view the moment it falls outside the window (Q2.9).
  *Default:* they persist. It is the reading that makes "MyDogsbody item" mean something distinct from "domain type", and templates already force a store to exist regardless.
- **Q5.8 — What makes two scans agree they found the same invoice?** Rescanning an overlapping window has to update rather than duplicate. Candidate keys: source message id (breaks when one mail carries two invoices), supplier + invoice reference (breaks if a supplier reuses numbers across years), or both together.
  *Default:* supplier + invoice reference as the natural key, source message id stored for traceability, and a unique index in the migration so the database refuses a duplicate even when the code is wrong.
- **Q5.9 — Where do the SQLite store functions live?** In `MyDogsbody.Database` alongside the context — which then needs a `ProjectReference` to `MyDogsbody.Domain` — or in a new outer-ring project referencing both?
  *Default:* in `MyDogsbody.Database`. It is already the main-database tier, the reference points inward so it breaks no rule, and a separate project per store is more ceremony than this earns.
- **Q5.10 — Do templates need export/import?** They will represent real effort to get right, and they'd currently live only in a SQLite file in `bin\Debug\net9.0\`. A JSON export would make them backup-able and reproducible after a clean rebuild.
  *Default:* not in the first pass, but keep the template model serialisable so adding it later is a page, not a redesign. Worth saying out loud that a `dotnet clean` should never be able to destroy a morning's template work.

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

§1.1 has closed eleven questions and opened twenty-five — 34 open before, 48 now. That is normal for a pre-proposal, and it is exactly why this file exists rather than a `requirements.md`: most of those twenty-five would otherwise have surfaced mid-implementation, when they cost more.

**Two independent starting points, either of which can begin now:**

- **Q5.7 → change #1.** Confirm invoices really persist, and `docs/changes/invoice-ledger-foundation/requirements.md` can be written immediately. Small change, no external dependency, and it proves the main SQLite database, its migrations and its store shape all work — which everything else leans on.
- **Q3.7–Q3.10 → change #5.** The credentials refactor depends on nothing and blocks the Google work. It is also the only change that makes the codebase *smaller*, so it is a good one to have behind you.

**§7.6 is the one to think hardest about.** It decides change #2, which is the largest and least precedented piece of the build, and no amount of design work elsewhere will de-risk it. If you want to sanity-check the template model before committing to it, the fastest test is to take two real invoices from two different suppliers and check whether the four rule kinds in §3.7 can actually locate every field.

The rest stay blocking only for the change that needs them: §7.1 → #4, §7.2 → #7, §7.3 → #6, §7.4 → #3. Everything in §7.5 apart from Q5.7 can be settled during design.

Requirements in EARS notation, agreed before any `design.md`, per CLAUDE.md.
