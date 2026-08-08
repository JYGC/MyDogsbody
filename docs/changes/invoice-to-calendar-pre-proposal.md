# Pre-proposal — Invoices from Thunderbird to Google Calendar

**Status:** pre-proposal, and **fully answered — §7 is empty.** Every question this document raised has been decided, the rule model has been measured against the real mailbox (§3.10, §3.11), and **all seven changes in §4 can now be specified.** The next artefact is a `requirements.md` in a change folder of its own, not another revision of this file.
**Date:** 2026-08-08, last measured and revised 2026-08-09

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
| **Q2.5** — the diff's time window | **The same window as the Thunderbird scan** | One knob, not two. It cannot be applied *literally* backwards, though: events sit on **due** dates, which run ahead of receipt. So the same N is mirrored around today and stretched to cover the latest due date in view — §3.2 |
| **Q2.6** — insert-only, or update and delete | **All three** | The diff stops being a status list and becomes a three-action **plan**. It also makes the sync **destructive**, which needs a hard guard (§5.18), and it reverses half of what Q2.4 bought — Q2.14 |
| **Q2.7** — upload everything, or a selection | **Both** — per-row checkboxes *and* a bulk button | The invoice table gains selection state; the button acts on the selection when there is one and on everything outstanding when there isn't |
| **Q2.8 / Q2.11 / Q2.12** — partial failure, unset calendar, per-upload override | **All three as proposed** | Continue past a failure and report per row; an account with no default calendar reads as *not ready* with the button disabled; no per-upload calendar override |
| **Q3.1 / Q3.2 / Q3.5 / Q3.6** — the OAuth flow | **All four as proposed** | System browser + loopback via `GoogleWebAuthorizationBroker`, as the prototype already does; one app-wide client secret pasted once; the `userinfo.email` scope so accounts show their address; removing an account deletes the local token and does not revoke at Google |
| **Q3.7** — what happens to `MyDogsbody.Domain/Credentials/` | **It stops being a domain area** | Nothing in the domain ever reasoned about a credential. Deletes `CredentialsTypes.fs` and three green workflows with their tests — a real coverage loss to state plainly in change #5 |
| **Q3.8** — do `InfrastructureType` and `Infrastructure` go too | **Both, and `MyDogsbody.Enums` with them** | The database identifies the provider, so the discriminator was a second source of truth. Also removes a pair of edge mappers and drops `Enums` out of `UI.Portal`'s reference set |
| **Q3.9** — the rows already in `Credentials.db` | **Discard** | No migration. It is a development database in `bin\Debug\net9.0\` holding a handful of retypeable rows; say so in the change rather than letting the file quietly stop being read |
| **Q3.10** — does `/settings/credentials` disappear | **Yes, entirely** | With `CredentialsComponents`, `CredentialsBrowserModule*` and their tests. The Google accounts page *is* Google's credential page. **§7.3 is now empty** |
| **Q5.4** — is the seven-change breakdown right | **Seven, in the order given** | The four-change alternative is off the table. §4 stands as written |
| **Q5.6** — encrypt OAuth tokens at rest | **Deferred, explicitly** | An accepted risk, not an oversight — it must be written into change #6's description so it is a decision on the record rather than something nobody got to. §5.5 |
| **Q5.10** — template export/import | **Not in the first pass**, but the model stays serialisable | Already true: templates are relational rows, so an export is a read and a write, not a redesign. §3.7 |
| **Q5.11** — how much provenance an invoice keeps | **`SourceMessageId` and nothing else** | No account or folder name on the ledger. Thunderbird's vocabulary stops at the integration boundary; a diagnostic view asks the integration from the message id |
| **Q5.12** — can an invoice be corrected or deleted by hand | **Delete yes, edit no** | A `DeleteInvoice` workflow and a per-row action. It also opens the hole that Q5.14 is about: deleting is only meaningful if the next scan doesn't put it back |
| **Q5.13** — typed settings table or key/value | **Typed** | `InvoiceSettings`, one row, a column per setting, a migration to add one. §3.6 |
| **Q1.5** — a message that yields no invoice | **Listed with the specific reason. Never silent, never fatal to the scan** | The four causes are the diagnostics that tell you which template to fix, so they are a feature of the templates workflow rather than noise. Needs somewhere to live, though — Q1.19 |
| **Q1.9** — does changing the window rescan immediately | **Immediate** | Taken with the caveat I raised against my own default: it is only tenable on the back of Q4.10's incremental scanning, and change #4 must **measure** a real scan before this is treated as settled. If a window change costs seconds, an explicit Refresh comes back |
| **Q1.10** — an invoice with no due date | **Stored and listed, greyed out with a reason. Not uploadable** | Already modelled: `UploadableInvoice` is a separate stage type, so the sync workflow cannot be handed one without a due date. The record of the invoice survives whether or not it can become an event. §3.2 |
| **Q1.12** — "DOC" | **`.docx` only** | NPOI never enters the solution and §5.8 stops being a risk. A legacy `.doc` attachment becomes a *listed problem* rather than a silent skip, so you find out if one ever actually arrives. `WordDocumentReader` is now a day's work, not a week's |
| **Q1.13** — email bodies | **Both, preferring `text/plain`** when the message carries that alternative, stripping HTML only when it doesn't | Keeps the text a template sees as close to what the sender wrote as possible. Multipart selection is the reader's job, so a template never has to know which alternative it got |
| **Q1.14** — absorb `Integrations.Pdf` | **Yes** — it becomes `MyDogsbody.Integrations.Documents` | Rename the project, move `PdfDocumentReader.fs`, keep its tests. One project per *capability* rather than per library, so the composition root binds `ReadDocumentText` once, in one place, for all four formats |
| **Q1.15** — how many invoices | **Assume hundreds** | No server-side paging in the first pass, and `MudTable`'s client-side paging is enough. It also means a full rescan stays a "press it whenever" action rather than one you schedule — which is the assumption Q1.9 is riding on |
| **Q1.16** — the largest window a user may add | **3650 days**, minimum 1 | A typo guard rather than a policy: big enough never to obstruct a real intention, small enough that 14000 typed for 1400 is rejected instead of walking the whole 16 GB store. The bound lives in `ScanWindowDays.create` and nowhere else |
| **Q1.6** — which date the window measures | **The date the mail arrived**, with the picker labelled to say so | The one reading that lets the reader skip a message on its `Date` header before touching its body or attachments — which is what makes a 7-day window cheap over a 16 GB store. It also confirms why §3.2 mirrors the calendar range rather than reusing the window literally: this one looks backwards at *receipt*, the events sit forwards on *due* dates |
| **Q1.11** — how a reader receives an attachment | **Bytes**, via a new `ReadDocumentText`. `ReadDocumentContent` and its coordinate-bearing `Word`s stay | No temp file per attachment per scan, and nothing existing breaks: `ReadDocumentLinesWorkflow`, its contract suite and the `PdfProcessing` scratch project all keep working. **§5.7 is closed** — the two types coexist and `PdfDocumentReader` satisfies both |
| **Q1.17** — what can be done to the window list | **Add and delete, no edit**, seeded rows as deletable as any other; the last one cannot be deleted; deleting the selected one falls back to 14, or to the shortest still present | Already the design in §3.2: `CannotDeleteLastScanWindow` as a domain error rather than a UI guard, and `ResolveScanWindowWorkflow` owning the fallback in one place. Confirms why the remembered choice is a number rather than a foreign key |
| **Q1.18** — is the cutoff measured from the start of today | **Start of day** (`getCurrentTime().Date`) | "The last 14 days" names a set of dates rather than 336 hours, so the same window scanned at 09:00 and 17:00 covers the same mail. Makes a rescan within one day genuinely idempotent, which Q4.10's watermarks also prefer |
| **Q1.19** — do scan problems persist | **Yes** — in `ScanProblems`, keyed by source message id, cleared when that message later yields an invoice | Without it, Q4.10's incremental scanning empties the list the moment you rescan and the diagnostic is gone before you look. It also gives Q7.6.7's "reprocess this supplier" something to work from: the rows name which messages are worth re-reading after a template change, instead of a full pass over 6.2 GB. **§7.1 is now empty** |
| **Q2.9** — does the window bound import, view, or both | **Both**, with the window and count stated above the table | Narrowing hides, it does not forget — nothing is deleted from the ledger, and the sync button acts only on what is visible. That sentence is now load-bearing rather than tidy: §5.18's first hazard is exactly what happens if it stops being true |
| **Q2.10** — what goes inside the extended property | **Supplier id + invoice reference**, under one well-known property name, with the local invoice id alongside for diagnostics only | The Q5.8 natural key rather than the database id, so rebuilding the ledger from scratch does not make every event read as missing. That key now appears in three places — see `InvoiceSyncKey` in §3.2 |
| **Q2.13** — what makes an event eligible for deletion | **The invoice left the ledger**, and the delete happens **on the next sync**, visible in the plan before you press it | Deleting a row in one app does not silently reach into another. A plan you can see before it runs is the difference between trusting this feature with delete permission and not |
| **Q2.14** — does a sync overwrite a hand-edited event | **Yes — the event is app-owned and always wins.** Title and date are rewritten | The honest cost of Q2.6: Q2.4 chose the extended property so a rename would not cause a duplicate, and it now also means a rename gets reverted. Justified because a title disagreeing with the ledger is worse than a lost edit — but the page should not pretend otherwise |
| **Q5.14** — does a hand-deleted invoice stay deleted | **Yes** — a **tombstone** on the natural key, which the scan skips. Visible and reversible | Without it "delete" meant "hide until the next scan", which is the failure mode that ruled out hand-editing in Q5.12. **§7.2 and §7.5 are now empty, and §7.6 is all that remains** |
| **Q7.6.1** — which rule kinds | **The seven in §3.11**, not the original four | `LinesAfterLabel` is the workhorse; `SubjectCapture`, `AttachmentName` and `DateFromField` were each added on measured evidence. The last of them is what takes complete extraction from 12% to 39% |
| **Q7.6.2** — raw regex on the page | **Keep it**, with the mandatory match timeout | It fired exactly once in 1,199 candidates, so it is a genuine escape hatch. Its editor is the **last** one built in change #2, not the first |
| **Q7.6.3** — several templates per supplier | **Filter by document part, try in your order, first complete match wins**, and record which template produced each invoice | Settled by data rather than preference: one water utility labels the same field `Due date` for most customers and `Direct debit` for direct-debit ones |
| **Q7.6.4** — two suppliers match one message | **An error against that message**, shown in the table | Silently picking one is how a month of invoices ends up filed under the wrong supplier. It lands in `ScanProblems` (Q1.19) like any other cause |
| **Q7.6.5** — how a supplier is matched | **Sender address, sender domain and subject pattern; several per supplier; matching on any** | Sender domain will do most of the work. The matcher stays on the supplier, not the template — §3.2 |
| **Q7.6.6** — is the test panel in scope | **Yes**, and it must show the text **after normalization** | Three of the failure modes in §3.10 are silent non-matches indistinguishable from "this supplier sends nothing". The panel has to show the text the rules actually see, or it agrees with a broken template |
| **Q7.6.7** — a template changes, what happens to invoices it made | **Leave them**; the next scan of a covering window updates them via the Q5.8 key | A reprocess button is a follow-up, and Q1.19 makes it cheap when it comes — the problem rows name exactly which messages to re-read |
| **Q7.6.8** — currency | **`FixedValue` per template**, overridable by a rule | 96% of documents carry `$` and every one sampled is AUD. Nothing in the sample needs the override. **§7.6 is now empty, and so is §7** |

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
- **`PaymentTermDays`** — how long after issue this supplier's invoices fall due, used by §3.11's `DateFromField` when a document states no due date. A supplier-level fact for the same reason the matcher is one, and the difference between a calendar that gets 12% of your invoices and one that gets 39%.
- Dependency types: `LoadSuppliers`, `SaveSupplier`, `UpdateSupplier`, `DeleteSupplier`.

Keeping the matcher on the **supplier** rather than on the template is deliberate: "is this mail from Acme?" is a fact about Acme, while a template answers the different question "given it *is* Acme, where are the fields?". Splitting them lets one supplier own several templates — which you will want the first time a supplier changes their invoice layout — without repeating the matching rules in each.

**`InvoiceTemplates/InvoiceTemplatesTypes.fs`** — the rule model, written out in full in **§3.7**
- `TemplateId`, `DocumentPart`, `FieldRule`, `TargetField`, `ParseHint`, `TemplateFieldRule`, and the stage types `UnvalidatedTemplate` → `ValidTemplate` → `StoredTemplate`.
- `TemplateError` (`PatternInvalid`, `DateFormatInvalid`, `RequiredFieldHasNoRule`, `RuleTimedOut`) and the dependency types `LoadTemplatesForSupplier`, `SaveTemplate`, `UpdateTemplate`, `DeleteTemplate`.
- `ValidTemplate` is the type that matters: it can only be produced by validating an `UnvalidatedTemplate`, and `ApplyTemplateWorkflow` accepts nothing else. That is how a runtime-authored rule still gets a compile-time guarantee at the point it's used.

**`Invoices/InvoicesTypes.fs`**
- Constrained primitives: `InvoiceReference` (the supplier's own invoice number — **`create` folds internal whitespace**, because §3.10 found a utility that prints its reference in space-separated groups and names the attachment with the same digits unspaced, which under Q5.8 would otherwise be two keys for one invoice), `Money` (amount + currency), `IssueDate`, `DueDate`, `SourceMessageId`. **`SupplierName` is not here** — an invoice carries a `SupplierId`, per §1.1.
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
      let Maximum = 3650                        // a typo guard, not a policy (Q1.16)

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
  - **`ReadDocumentText = DocumentSource -> Result<TextLine list, DocumentError>`** — one type for all four formats. It is declared in `Documents/`, **beside the `ReadDocumentContent` that Q1.11 keeps**, rather than here: reading a document is that area's concern, and a reader that spoke `InvoiceError` would be a PDF reader only invoices could use. The invoice workflow maps `DocumentError → InvoiceError` as it binds, the same way it already borrows `LoadSuppliers` from a sibling area. Where

    ```fsharp
    type DocumentFormat = Pdf | Word | PlainText | EmailBody

    // Chosen by filename extension, never by Content-Type: §3.10 measured 155 of 644 PDFs
    // arriving as application/octet-stream and 4 as application/.pdf. Dispatching on the
    // declared type would misroute a quarter of them.

    /// Bytes, not a path: an attachment lives inside an mbox, and it should not have to be
    /// spilled to a temp file (and cleaned up, and kept out of a backup) to be read.
    type DocumentSource = { Format: DocumentFormat; Name: string; Content: byte[] }
    ```

    The composition root binds one reader per format and dispatches on `Format`, so the domain sees a single function and adding a fifth format never touches a workflow. **This does not match the existing `ReadDocumentContent`**, which takes a `DocumentPath` and returns coordinate-bearing `Word`s — see Q1.11, it is a real decision, not a detail.
  - **The store, as functions**: `LoadInvoices = ScanCutoff option -> Result<StoredInvoice list, InvoiceError>`, `UpsertInvoices = ValidInvoice list -> Result<StoredInvoice list, InvoiceError>`, `MarkSynced = InvoiceId -> CalendarEventId -> Result<unit, InvoiceError>` and — once Q2.6 allows a delete — `ClearSyncRecord = InvoiceId -> Result<unit, InvoiceError>`. Q5.12 adds `DeleteInvoice = InvoiceId -> Result<unit, InvoiceError>` and no `UpdateInvoice`: an invoice can be removed by hand but not corrected by hand, so there is no path by which a typed-in value can be silently overwritten by the next scan. Upsert rather than insert, because rescanning the same window must not double the ledger — see Q5.8 for what makes two scans agree they found the same invoice.
  - **The scan windows, as functions**: `LoadScanWindows = unit -> Result<StoredScanWindow list, InvoiceError>`, `SaveScanWindow = ValidScanWindow -> Result<StoredScanWindow, InvoiceError>`, `DeleteScanWindow = ScanWindowId -> Result<unit, InvoiceError>`, plus the remembered choice: `LoadSelectedScanWindow = unit -> Result<ScanWindowDays option, InvoiceError>` and `SaveSelectedScanWindow = ScanWindowDays -> Result<unit, InvoiceError>`. The `option` is the honest shape — a fresh database has no choice recorded, and that is what `fallback` is for.

  From the `Suppliers` and `InvoiceTemplates` areas the invoice workflows also take `LoadSuppliers` and `LoadTemplatesForSupplier`. A workflow taking a dependency declared in a sibling area is fine — they are all inside `MyDogsbody.Domain`, and the alternative is duplicating the type.

**`Calendar/CalendarTypes.fs`**
- `GoogleAccountId`, `CalendarId`, `CalendarEventId`, `CalendarEvent`, and `InvoiceSyncKey` — the idempotency key, settled by Q2.4 as the value stamped into a private extended property and by Q2.10 as **supplier id + invoice reference**, the Q5.8 natural key. It is a domain type precisely because both sides of the diff have to derive it the same way — and that now matters three times over, because the same key identifies a row in the unique index, an event on the calendar, and a tombstone (Q5.14). **One function derives it, and everything else calls that function.** Three hand-rolled derivations of the same key would agree right up until one of them didn't.
- `AllDayEvent` — start date and title/description only. Q2.1 makes every invoice event all-day on the due date, so there is no reason for the domain to carry times, time zones or durations it never sets. Keeping them out means the mapper cannot accidentally invent one.
- `RegisteredGoogleAccount` carries its **default invoice calendar** (Q2.3): `{ Id; EmailAddress; DefaultInvoiceCalendar: CalendarId option }`. The `option` is not laziness — a freshly authorised account genuinely has no calendar chosen yet, and per Q2.11 that state renders as *not ready* with the sync button disabled, rather than as a failure at the API.
- **`CalendarDateRange`** — the bound `Events.list` needs, `private CalendarDateRange of DateTime * DateTime`. Q2.5 ties it to the scan window, which needs saying carefully, because the obvious reading is wrong: the scan window looks **backwards** at when mail arrived, while an invoice event sits on its **due date**, which is normally ahead of that. Querying `[today - N, today]` would therefore miss the event for every invoice not yet due, read it as absent, and create a second one. So the same N drives both, mirrored: `[today - N, today + N]`, **stretched forward if any invoice in view falls due later than that** — a supplier on 60-day terms inside a 14-day window is not exotic. One number, one setting, no second knob on the page; it just cannot be applied in one direction only.
- **`SyncAction`** — what Q2.6 turns the diff into:

  ```fsharp
  type SyncAction =
      | CreateEvent of UploadableInvoice
      | UpdateEvent of CalendarEventId * UploadableInvoice   // event disagrees with the ledger
      | DeleteEvent of CalendarEventId                       // the invoice is gone from the ledger
      | LeaveAlone  of CalendarEventId                       // identical - no API call at all
  ```

  Per Q2.14 an `UpdateEvent` rewrites the title and the date unconditionally: the event is app-owned, so "disagrees with the ledger" covers both an invoice that changed and an event somebody edited by hand, and the workflow does not try to tell those apart. `LeaveAlone` is a case rather than an absence on purpose: it is what makes "sync twice, second one does nothing" assertable, and with update enabled that is no longer free the way it was under insert-only.
- Error DU: `CalendarError` — `CalendarUnreachable`, `NotAuthorised`, `AccountNotRegistered`, `NoDefaultCalendar of GoogleAccountId`, `CalendarNoLongerExists of CalendarId`, `EventRejected`, and now `EventNoLongerExists of CalendarEventId` — an update or delete aimed at an event somebody already removed by hand, which is expected rather than exceptional and must not fail the batch.
- Dependency function types: `ListGoogleAccounts`, `RegisterGoogleAccount`, `SetDefaultInvoiceCalendar`, `ListCalendars`, `ListCalendarEvents` (now taking a `CalendarDateRange`), `CreateCalendarEvent`, and per Q2.6 **`UpdateCalendarEvent`** and **`DeleteCalendarEvent`**.

**One consequence of Q2.4 worth stating plainly:** a private extended property only exists on events *this app* created. An event you added to the calendar by hand for the same invoice carries no property, so the diff will not see it and the upload will create a second one. That is the correct trade for robustness against renaming — but it is the behaviour, so the page should not claim to detect "any event for this invoice".

**Q2.6 turns half of that trade around, and it is worth seeing before it is built.** Under insert-only, "the property survives you renaming the event" meant *we will not duplicate it*. With update enabled it also means *we will rename it back* — the app can now find your edited event and overwrite the edit. Same for moving it to another date. That is defensible if an invoice event is app-owned data with no business being hand-edited, and it is infuriating if you moved it deliberately. **Q2.14.**

**Workflows** (one file each, one public function each):
- **`ApplyTemplateWorkflow` — pure, no dependencies**: `Template -> ScannedMessage -> Result<UnvalidatedInvoice, InvoiceError>`. The rule engine. Everything that makes template editing worth having is decided here, and none of it touches a file, a clock or a network. Table-driven unit tests over `(template, text) → expected fields` are the cheapest coverage in the whole feature, and they'll be where template bugs actually get found.
- **`MatchSupplierWorkflow` — pure**: `StoredSupplier list -> ScannedMessage -> Result<SupplierId, InvoiceError>`. Also decides what happens when two suppliers match — Q7.6.4.
- **`ResolveScanWindowWorkflow` — pure**: `StoredScanWindow list -> ScanWindowDays option -> ScanWindowDays`. Given what exists and what was remembered, decide which window the page opens on. Three cases, one function, one place: nothing remembered → `fallback`; remembered and still in the list → that one; remembered but since deleted → `fallback`, or the shortest window still present if 14 has itself been deleted. Small enough to look not worth naming, and exactly the sort of rule that otherwise ends up half in a module creator and half in a mapper.
- `ScanForInvoicesWorkflow` — `getCurrentTime → readMailFolder → readDocumentText → loadSuppliers → loadTemplates → upsertInvoices → (account, ScanWindowDays) → ScanResult`. The orchestration: work out the cutoff, pull the messages, flatten each to text, match a supplier, apply its template, validate, store. **`ScanResult` carries two lists, not one** — `{ Invoices: StoredInvoice list; Problems: ScanProblem list }` — because Q1.5 makes a message that yielded nothing a *result* rather than a silence. A `ScanProblem` is the message identity plus one of the four causes (no supplier matched, no template for that supplier, a rule found nothing, an attachment was unreadable, or its format is unsupported — Q1.12's `.doc` case), and none of them stops the scan. Note how much of it is calls to the two pure workflows above — that is the shape to aim for. The cutoff arithmetic (`getCurrentTime().Date.AddDays(-days)`) is a private pure function in this file, so "180 days back from 5 January" is a unit test with a fixed clock and no mail store anywhere near it. Days are uniform in a way months are not, which quietly removes the month-end trap flagged in §5.15.
- `DiffInvoicesAgainstCalendarWorkflow` — **pure**: `StoredInvoice list -> CalendarEvent list -> SyncAction list`. Still the heart of the feature and still the cheapest thing to test, provided the match key is a value and not a heuristic. Q2.6 makes it produce a *plan* rather than a status, which is the better shape anyway: every decision about what to touch is made here, in a function with no network in it, and the outer workflow only executes.
  **The one rule this workflow must not break:** a `DeleteEvent` is only ever produced for an invoice that has *left the ledger*. An invoice that simply falls outside the current window is not gone — it is not being looked at. Get that wrong and narrowing the picker from 180 days to 7 deletes six months of calendar entries, which is the worst thing this feature could possibly do. It is one condition, and it is the single most important test in change #7. §5.18.
- `SyncInvoicesToCalendarWorkflow` (was `UploadInvoicesToCalendar…`, renamed because it no longer only uploads) — takes the plan, calls `CreateCalendarEvent`, `UpdateCalendarEvent` or `DeleteCalendarEvent` per action, skips `LeaveAlone` entirely, and records the outcome: `MarkSynced` after a create, `ClearSyncRecord` after a delete. Per Q2.8 it continues past a failure and returns a per-action outcome list rather than stopping at the first one; an `EventNoLongerExists` on an update or delete is a success, not a failure — the calendar already agrees with where we were trying to get to.
- Supplier and template CRUD: `AddSupplierWorkflow`, `EditSupplierWorkflow`, `DeleteSupplierWorkflow`, `ListSuppliersWorkflow`, and the same four for templates. These are the same shape as the existing `AddCredentialWorkflow` / `EditCredentialWorkflow` and should be written by copying them.
- `DeleteInvoiceWorkflow` (Q5.12) — removes an invoice from the ledger, and is the *only* hand-driven change to one. Two things hang off it that are easy to miss: per Q2.13 the next sync turns that into a `DeleteEvent`, and per Q5.14 it is an open question whether the next scan simply puts the invoice back.
- Scan-window CRUD, the same shape again but smaller: `AddScanWindowWorkflow` (rejects a duplicate and anything outside `create`'s bounds), `DeleteScanWindowWorkflow` (rejects deleting the last one), `ListScanWindowsWorkflow`, `SelectScanWindowWorkflow` (persists the choice). No edit workflow — a window is one number, so changing it is a delete and an add, and offering an edit would just be a second path to the same duplicate check.
- `ListMailAccountsWorkflow`, `SelectMailAccountWorkflow`, `RegisterGoogleAccountWorkflow`, `ListGoogleAccountsWorkflow`.

### 3.3 Outer ring

**`MyDogsbody.Integrations.Thunderbird`** (new) + `.Database.Models` (C#, if it stores anything)
- `ThunderbirdFolderScanner.fs` — **the entry point, per Q4.2.** Given the root folder you chose, walk it recursively for `prefs.js` files; each one found is a profile root. No `profiles.ini` lookup and no `%APPDATA%` assumption. Handles the folder being one profile, a parent of several, or a backup copy.
- `ThunderbirdAccountReader.fs` — `prefs.js` → accounts. **The authoritative list is `mail.accountmanager.accounts`, not the directory tree** — verified against the real profile in **§3.8**, where a directory walk would have found 15 IMAP accounts where 9 exist. Store paths come from `directory-rel`, never from `directory`, which was measurably stale.
- `MailFolderReader.fs` — **mbox *and* maildir, both required per Q4.3**, though the real profile is 100% mbox, so maildir ships against synthetic fixtures only (Q4.11). Format per account comes from `storeContractID`. MIME parsing (MimeKit is the realistic pick) for attachments. **Takes the cutoff and honours it while reading**: per Q1.6 the window measures the **received** date, so a message is skipped on its `Date` header before its body or attachments are touched — which is what stops a 7-day window paying for a decade of mailbox. A message whose `Date` is missing or unparseable is **included**, not skipped: excluding it would be silent data loss with nothing on screen to show for it, whereas including it produces at worst a stored row you can delete.
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
- `PdfDocumentReader.fs` — PdfPig. **Already exists**, in `MyDogsbody.Integrations.Pdf`, with a contract suite. It does the overwhelming majority of the work: §3.10 found the invoice fields in the PDF 91% of the time and in the body 8%.
- **Format is decided by filename extension, not by `Content-Type`.** 24% of the PDFs in the real mailbox declare themselves `application/octet-stream`. §3.10.
- `WordDocumentReader.fs` — **`.docx` only** (Q1.12), via DocumentFormat.OpenXml. A legacy binary `.doc` is not read at all: it is reported as an unsupported-format problem against that message, which is how you find out whether one ever actually arrives.
- `PlainTextDocumentReader.fs` — decoding and line splitting; no library.
- `EmailBodyReader.fs` — the message body itself. Per Q1.13 it takes the `text/plain` alternative when the message carries one and strips markup only when it does not, so multipart selection stays the reader's problem and never becomes the template's.

**This project *is* `MyDogsbody.Integrations.Pdf`, renamed** (Q1.14): `PdfDocumentReader.fs` moves across with its contract suite intact and the three new readers join it. One project per *capability* rather than per library is what keeps the composition root binding `ReadDocumentText` once, in one place, for all four formats — and it is why a fifth format later is a file rather than a project.

**`MyDogsbody.Integrations.Google`** (exists as a stub)
- `GoogleCalendarClient.fs` — `CalendarService` behind `ListCalendars` / `ListCalendarEvents` / `CreateCalendarEvent` / `UpdateCalendarEvent` / `DeleteCalendarEvent`, lifted from the `GoogleCalendarCRUD` prototype. **Q2.6 costs nothing here**: the prototype is a CRUD prototype and already does all four operations against `CalendarService`. The cost of update-and-delete is entirely in the domain and the UI, not in the adapter.
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
  Q2.7 adds selection to the same module: `SelectedInvoiceIdsAval: aval<Set<string>>`, `ToggleInvoice`, `ClearSelection`, and a derived `PendingActionCountAval` the button renders. Selection is view state and is deliberately **not** persisted — it resets on a rescan, because a tick against a row that no longer exists is worse than no tick at all.

**`MyDogsbody.UI.Portal`** — six pages, registered in `Shell.fs`; the five settings pages also linked from `SettingsComponents.settingsNavMenu`:

| Page | Route | Contents |
| --- | --- | --- |
| Invoices | `/invoices` (top-level, not settings) | **Scan-window picker, rendering whatever windows the store holds and opening on the remembered choice**, Google-account picker — **no calendar picker**, the account carries its own (Q2.3), shown read-only beside it — refresh, invoice table with a per-row sync-status column (**four states now, not two** — up to date, missing, changed, orphaned) and **a per-row checkbox** (Q2.7), a "Sync to calendar" button that acts on the ticked rows or on everything outstanding when none are ticked and shows that count, per-row outcomes after a partial failure (Q2.8), **a per-row delete** (Q5.12 — delete only, no inline edit), a **"problems" view** listing the messages that yielded nothing and why (Q1.5), the window and count stated above the table so the list reads as a range rather than the whole ledger (Q2.9), a view of **tombstoned invoices** with an un-delete (Q5.14), and `MudAlert` from `ErrorAval` |
| Google accounts | `/settings/google-accounts` | Table of registered accounts, "Add account" → consent flow, **the default invoice calendar for each account, picked from a dropdown of that account's own calendars**, remove / re-authorise |
| Thunderbird accounts | `/settings/mail-accounts` | **The root folder to search** (Q4.2) with a Browse button, a "scan for accounts" action, the table of accounts the recursive walk found — name, email, store format, message count — and a radio to pick the one to import from |
| **Suppliers** | `/settings/suppliers` | Table of suppliers with their match rules; add / edit / delete via dialog; each row opens that supplier's templates |
| **Templates** | `/settings/suppliers/{id}/templates` | The templates for one supplier: field rules, and — strongly recommended — a **"test against a message" panel** that runs `ApplyTemplateWorkflow` over a real scanned message and shows what each rule extracted. See §3.7 |
| **Scan windows** | `/settings/scan-windows` | The list of windows in days — 7, 14, 30, 90 and 180 seeded — with add and delete, the currently remembered one marked, and the last remaining one undeletable. One number field per row and no edit action, per §3.2 |

Both new settings pages are the credentials page again: `MudTable` + toolbar button + `FunComponent` dialog + module creator with `cval`/`transact`. Nothing novel, which is the point — the novelty is all in §3.7.

The scan-window picker was going to be a `MudToggleGroup` of six fixed buttons. **It can't be, now that the count is unknown at build time** — five looks fine as buttons and twelve does not, and the component cannot know which it will get. So: a `MudSelect` bound to `AvailableScanWindowsAval`, which renders any number of windows without the toolbar deciding how many are reasonable. The component holds no list of its own either way — that part of the original argument survives, and matters more now that the list is genuinely variable. Per Q1.6 the label says what it measures — "mail received in the last 90 days", not "90 days" — because a bare number is exactly where someone assumes it means due dates.

### 3.6 Persistence — the main SQLite database, finally wired in

§1.1 makes suppliers, templates and invoices MyDogsbody items, so they go in `MyDogsbody.Database`. This is the change CLAUDE-project.md anticipated: *"the first change that needs the main database adds one in `Startup/Startup.fs` alongside the LiteDB contexts, writes store functions against it, and binds those to the dependency function types a workflow declares."*

**Tables** — each with its own FluentMigrator migration under `Migrations/Migration_<timestamp>_<Name>.fs`, in timestamp order above `MigrationSetup.fs`:

| Table | Holds |
| --- | --- |
| `Suppliers` | id, name, payment term days |
| `SupplierMatchers` | supplier id, kind (sender / domain / subject), value |
| `InvoiceTemplates` | id, supplier id, name, which document part it applies to, ordering |
| `TemplateFieldRules` | template id, target field, rule kind, pattern/label, parse hint |
| `Invoices` | id, supplier id, reference, amount, currency, due date (nullable), source message id, scanned date |
| `InvoiceCalendarEvents` | invoice id, google account id, calendar id, event id, last synced date |
| `ScanWindows` | id, days — **unique**, so 14 cannot be added twice |
| `InvoiceSettings` | a single row with its primary key fixed at 1 — the remembered scan window, in days |
| `ScanProblems` | source message id, supplier id (nullable), cause, detail, scanned date — the messages that yielded nothing, per Q1.5. **Persisted** (Q1.19), and a row is cleared when its message later yields an invoice |
| `InvoiceTombstones` | supplier id, invoice reference, when it was deleted — the Q5.14 record that keeps a hand-deleted invoice deleted |

`Blog` and `Comment` stay as they are — they're scaffold, and nothing here disturbs them.

**The five seeded windows are inserted by the migration that creates the table**, with `Insert.IntoTable`, and removed again by its `Down`. That keeps the rule CLAUDE-project.md states — the schema, and now its seed data, come from FluentMigrator and from nowhere else — rather than having `Startup.fs` check on every launch whether it ought to write five rows. The consequence to accept knowingly: if you delete a seeded window, re-running migrations will not bring it back. That is the correct behaviour for a value you chose to remove, and it is why `ScanWindowDays.fallback` exists rather than the code assuming 14 is present.

**`InvoiceSettings` is a single-row typed table, not a key/value store** — settled by Q5.13, and it matters because this is the first setting and whatever it does becomes the habit. A `Settings(Key, Value)` table would take one migration and never need another, at the cost of every setting being a string parsed at the point of use; a typed row costs a migration per setting and keeps the store honest about what it holds. Adding a column by migration is already the rule for every other schema change here, so this is the consistent answer rather than the clever one.

**`InvoiceCalendarEvents`** is the table worth arguing about. A sync record is a fact about *an invoice*, so it belongs on this side rather than in the Google integration's store — but it also means the app has an opinion about what's on a calendar that can go stale if you delete an event by hand. **The calendar remains the source of truth for the diff** (Q2.4's extended-property query), and this table is history: what we synced, when, to which account. Don't let it become the answer to "is it there?".

Q2.6 sharpens that. A delete clears the row (`ClearSyncRecord`), an update refreshes its date, and neither is allowed to become the thing the diff consults — if this table and the calendar disagree, the calendar is right and this table is out of date. Its actual job is diagnostic: *when did we last touch this event, and on whose calendar?*, which is the first question worth asking when a sync did something unexpected.

**The store functions live in `MyDogsbody.Database`** (Q5.9): it gains `SupplierStore.fs`, `TemplateStore.fs`, `InvoiceStore.fs`, `ScanWindowStore.fs` and their record ⇄ domain mappers, plus a `ProjectReference` to `MyDogsbody.Domain`. No new project. It is outer-ring code, so it keeps the outer-ring shape: dependencies first, input last, `Result<'T, MyDogsbodyException>`, written with `handleError`, one `ActionNames` entry per function.

**`InvoiceTombstones` needs to be visible on screen, not just present** (Q5.14). It is a filter applied inside `UpsertInvoices`, so a tombstoned key is silently skipped by every later scan — which is exactly right for junk from a marketing email that matched a supplier, and exactly wrong the day you delete a row because the *template* was broken, fix the template, and the corrected invoice under that same reference is then skipped too. A list you can see and un-tombstone costs one small page and removes the only way this feature can lie to you.

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
5. **Q5.10 asks the model to stay serialisable, and it already is.** Templates are relational rows (`InvoiceTemplates` + `TemplateFieldRules`), not an opaque blob, so a JSON export is a read and a write rather than a redesign. Worth keeping true: if a rule kind is ever added that needs a nested structure, it gets a column, not a serialised field smuggled into one. The point of the constraint is that a `dotnet clean` should never be able to destroy a morning's template work.

**§3.10 is this model measured against your actual mailbox** — 1,199 candidate messages and 644 PDFs — and **§3.11 is the rule set that came out of it.** It changes the shape above in four ways: three rule kinds are added, a normalization contract becomes mandatory, `TargetField` gains `IssueDate`, and the body demotes from a first-class source to a rounding error. Read §3.10 before answering §7.6; most of those questions now have evidence behind them rather than a preference.

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
| `MyDogsbody.Domain/Credentials/` — `CredentialsTypes.fs` and all three workflows (Q3.7) | Nothing. A credential stops being a domain concept; it is a token the Google adapter holds |
| `MyDogsbody.Enums`, `InfrastructureType` and the domain's `Infrastructure` union (Q3.8) | Nothing. The database identifies the provider |
| `/settings/credentials`, `CredentialsComponents`, `CredentialsBrowserModule*` and their tests (Q3.10) | The Google accounts page, which is already the place you manage Google's credentials |
| The rows in `Credentials.db` (Q3.9) | Nothing — discarded and retyped |

**The database is the provider, so the discriminator field goes.** This is the same argument CLAUDE-project.md already makes for the log store — *"the collection is the severity, do not add a `Severity` field"* — applied one tier up. A credential in `Google.db` is a Google credential; an `InfrastructureType` column beside it would be a second source of truth for a fact the file path already states. **Q3.8 settles that: the discriminator goes, and `MyDogsbody.Enums` goes with it** — a whole C# project whose only reason to exist was sharing `InfrastructureType` between F# and C#. The domain's own `Infrastructure` union goes too, and so does the pair of edge mappers that translated between them. `UI.Portal`'s reference set shrinks from three projects to two.

**The arithmetic of change #5**, since a refactor that deletes things should be honest about what it leaves: three projects removed — `Integrations.Credentials`, its `.Database.Models`, and `MyDogsbody.Enums` — against one added, `Integrations.Google.Database.Models`, which has to exist for the credentials to move into. The solution ends up two projects smaller, one domain area lighter, and with `/settings/credentials` gone from the nav.

**This must be its own change, and it must go first among the Google work.** It modifies code that is currently green at all four test levels, and CLAUDE.md is explicit: *"Existing behaviour you depend on but are not changing gets a characterization test before you change anything near it."* Folding this into `google-account-integration` would mean a change that simultaneously deletes a store, moves a UI page, rewires the composition root and adds OAuth — with no clean point to check the suite. Split, it is a boring refactor followed by an ordinary feature.

### 3.10 The rule set — measured against the real mailbox

Measured on 2026-08-09 against `C:\Users\jygcn\AppData\Roaming\Thunderbird\Profiles\49stkd1y.default`, the same profile as §3.8. This is the exercise §8 kept asking for: §3.7's rule model tested against real invoices instead of imagined ones. **Everything below is a count, not a guess.**

No invoice contents are reproduced here — supplier names and layout shapes only. Amounts, references, account numbers and addresses stay out of version control on purpose.

#### What was scanned

| | |
| --- | --- |
| In-scope mbox files walked | **505** (Trash / Junk / Deleted / Sent / Drafts excluded per Q4.8) |
| Messages whose headers were parsed | **234,446** |
| Invoice-like candidates (subject or sender) | **1,199** |
| PDF attachments extracted and read | **644** |
| Suppliers appearing more than twice | **~30** |

The header pass over 234,446 messages ran in minutes on a cold cache, which is the first practical confirmation that Q1.9's immediate-rescan and Q4.10's watermarks are on solid ground: **skipping on headers is cheap, and opening bodies is what costs.**

#### Finding 1 — the data is in the PDF, and almost never in the body

This inverts an assumption §3.7 was written on. Per document that contained readable text:

| Field found by any label rule | In PDF attachments (n=558) | In email bodies (n=1,164) |
| --- | --- | --- |
| Invoice reference | **91%** | 8% |
| Amount | 77% | 8% |
| Due date | 22% | 2% |

**The body is not a secondary source, it is a rounding error.** Of the four `DocumentPart` values in §3.7, `Attachment Pdf` does essentially all the work. The two largest suppliers by volume both send a body that says, in full, some variant of *"attached is your invoice"*.

`.pdf` is also effectively the only attachment format that matters: **644 PDFs, 114 `.xlsx`, 1 `.docx`, 0 legacy `.doc`**. Q1.12 chose `.docx`-only and the mailbox says even that is close to dead — while spreadsheets, which no reader is planned for, outnumber Word documents 114 to 1. None of the `.xlsx` files sampled were invoices (they are property-management statements), so this is not a call to build a fourth reader; it is a reason to make the unsupported-format problem row from Q1.12 say *which* format, so the question can be answered from data later.

#### Finding 2 — PDF text extraction works, and needs no OCR

| | |
| --- | --- |
| PDFs with an extractable text layer | **610 / 644 — 94.7%** |
| PDFs with no text layer (scanned images) | **10 — 1.6%** |
| PDFs that failed to open at all | **24 — 3.7%** |
| Single-page | 441 · two-page 150 · three or more 51 |

**No OCR is required**, which removes the largest cost risk in the extraction path. The 34 unreadable PDFs are a `ScanProblem` row each (Q1.5), not a reason to add Tesseract.

#### Finding 3 — the due date is the binding constraint, and it is worse than expected

Across the 558 PDFs with readable text, testing every label variant for each field:

| Field | Found |
| --- | --- |
| Invoice reference | 75% |
| Amount | 76% |
| Issue / invoice date | 48% |
| **Due date** | **19%** |
| Reference **and** amount | 69% |
| **All three, including a due date** | **12%** |
| Reference, amount, and *any* date | 39% |

**Only about one invoice in eight states a due date.** Q2.1 puts an all-day event on the due date and Q1.10 stores a due-date-less invoice as *not uploadable* — so on this mailbox, as specified, roughly seven invoices in eight would arrive in the ledger greyed out and the calendar would stay nearly empty. That is not a defect in the design; it is what the source data contains, and it is much better known now than after change #7.

**The fix is one rule kind.** 48% of documents carry an issue or invoice date, and payment terms are a property of the supplier rather than of the document. Deriving the due date from the issue date plus the supplier's terms lifts complete coverage from **12% to 39%** — a 3.2× improvement for one rule and one number per supplier. It is the single highest-value change this scan suggests.

#### Finding 4 — normalization is mandatory, and its absence is invisible

Three separate ways the text arrives unusable, each of which silently produces "rule matched nothing" rather than an error:

1. **Non-breaking spaces.** The largest supplier's PDF renders as `Invoice\u00a0Number:\u00a07422`. A rule written as `AfterLabel "Invoice Number:"` never matches, and nothing on screen explains why.
2. **Hard-wrapped labels.** One utility's email wraps `Invoice number` across two lines and splits the value across two more — in *both* the plain-text and HTML alternatives, because the wrapping is in the source. Label-anchored rules cannot see a label that is not on one line.
3. **Letter-spaced headings.** PDF text extraction returns `TA X INVOICE` where the document shows `TAX INVOICE`, because the letters are individually positioned.

So the rule engine needs a **defined normalization contract, applied before any rule runs** and identical at authoring time and scan time:

- Unicode NFKC, then fold `U+00A0`, `U+2007`, `U+202F` and friends to a plain space.
- Collapse runs of spaces and tabs; strip leading and trailing space per line.
- Join a wrapped continuation line to its predecessor **within a block**, where a block is a table cell or paragraph — never across blocks, or `LinesAfterLabel` loses the structure it depends on.
- Drop empty lines before applying line offsets, so `LinesAfterLabel(label, 1)` means "the next line with content".

Nothing in §3.7 said any of this, and every one of the three would have produced a template that worked in the test panel and failed in production, or vice versa.

#### Finding 5 — Q1.13's "prefer text/plain" is wrong for this mail

589 of 1,199 candidates carry both a `text/plain` and a `text/html` alternative. Where the body matters at all, the HTML is the **better** source, because the table structure keeps a label and its value adjacent while the plain-text alternative has already thrown that away by wrapping. Given Finding 1 this affects few invoices, so it is a correction rather than a crisis — but the reasoning behind Q1.13's default does not survive contact with the data, and the answer should be "prefer whichever alternative preserves block structure", which in practice means HTML.

---

### 3.11 The rule set

The deliverable: what §3.10's measurements say the template DSL should be. Changes from §3.7 are marked.

#### Kept, and validated by the data

| Rule | Evidence |
| --- | --- |
| `LinesAfterLabel of label * offset` | **The dominant kind.** Yarra Valley Water, Xero and OC Energy all print the label on its own line with the value beneath. Offset is almost always 1 |
| `AfterLabel of label` | IODM's whole invoice is `Label: value`; Xero uses it for the due date in the same document where the reference is label-above-value |
| `RegexCapture of pattern` | Earns its place as the escape hatch — one supplier's body is a single sentence carrying both reference and amount, which no label rule can decompose |
| `FixedValue` | 96% of documents carry `$` and every one sampled is AUD. Currency as a fixed value per supplier is right, confirming Q7.6.8 |

**A template must allow a different rule kind per field.** Xero proves it: label-above-value for the reference and inline `Label: value` for the due date, in one document. §3.7 already models this correctly — each `TemplateFieldRule` carries its own `Rule` — so nothing changes, but it is now a requirement rather than an accident.

#### Added, each for a measured reason

```fsharp
type FieldRule =
    | AfterLabel        of label: string
    | LinesAfterLabel   of label: string * offset: int
    | RegexCapture      of pattern: string
    | FixedValue        of string
    // added after measuring the real mailbox:
    | SubjectCapture    of pattern: string          // the reference is in the Subject
    | AttachmentName    of pattern: string          // ...or in the attachment filename
    | DateFromField     of source: TargetField      // + the supplier's payment terms
```

- **`SubjectCapture`** — **209 of 1,199 candidates (17%) carry the invoice reference in the subject line**, and §3.7 had no way to read it. One supplier's subject states the reference explicitly while its PDF states it only in a table.
- **`AttachmentName`** — the largest supplier by volume names every attachment for its invoice number and nothing else; a utility names the file with the same reference its PDF prints. This is the most reliable single field source found in the whole scan, and it costs nothing to read.
- **`DateFromField`** — the 12% → 39% rule from Finding 3. `DateFromField IssueDate` means *due this supplier's payment terms after the issue date the template already extracted*. It needs `IssueDate` added to `TargetField`, which is worth having regardless: 48% of documents state one.

  **The term length lives on the supplier, not on the rule** — `StoredSupplier.PaymentTermDays`. "Acme bills net 30" is a fact about Acme in exactly the way "is this mail from Acme?" is, which is the same argument §3.2 already makes for keeping matchers on the supplier. Putting the number on the rule would let one supplier's two templates (Q7.6.3) disagree about when its own invoices fall due.

`TargetField` becomes `Reference | Amount | Currency | IssueDate | DueDate`.

#### Two hazards the DSL must handle, not the template author

1. **Ambiguous numeric dates.** 26% of dates are `d/m/yyyy` and 5% are `d/m/yy`. On Australian mail `02/08/2016` is 2 August; read as US month-first it becomes 8 February — **a six-month error that silently lands an event on the wrong day**. `ParseHint.AsDate` must therefore take an explicit format string and never fall back to `DateTime.Parse` with ambient culture. Dominant formats, in order: `d MMM yyyy` (63%), `d/M/yyyy` (26%), `d/M/yy` (5%), `MMM d, yyyy` (3%).
2. **References printed with grouping spaces.** One utility prints its invoice number in three space-separated groups while naming the attachment with the same digits unspaced. Under Q5.8 those are two different natural keys for one invoice, so **`InvoiceReference.create` must normalize internal whitespace** — otherwise the same invoice read from the PDF and from the filename produces two ledger rows and two calendar events.

#### Worked templates

Shapes only, values redacted. These are the four suppliers whose layouts proved consistent across every sample:

| Supplier | Part | Reference | Amount | Dates |
| --- | --- | --- | --- | --- |
| Invoice-management platform (271 msgs) | `Attachment Pdf` | `AfterLabel "Invoice Number:"` — or `AttachmentName "^(\d+)\.pdf$"`, which is equally reliable and cheaper | `LinesAfterLabel ("Total", 1)` | `AfterLabel "Date:"` as **issue** date; no due date is ever printed, so `DateFromField (IssueDate, terms)` |
| Water utility (52 msgs) | `Attachment Pdf` | `LinesAfterLabel ("Invoice number", 1)` | `LinesAfterLabel ("Amount due", 1)` | `LinesAfterLabel ("Due date", 1)`, **and a second template** using `"Direct debit"` |
| Accounting-platform invoices (15 msgs) | `Attachment Pdf` | `LinesAfterLabel ("Invoice Number", 1)`, or `SubjectCapture` | `LinesAfterLabel ("Amount AUD", 1)` | `AfterLabel "Due Date"` inline; `LinesAfterLabel ("Invoice Date", 1)` |
| Embedded-network energy retailer (15 msgs) | `Attachment Pdf` | `LinesAfterLabel ("Invoice Number", 1)` | `LinesAfterLabel ("Total Amount Due", 1)` | `LinesAfterLabel ("Due Date", 1)` and `"Date of Issue"` |

**The water utility needs two templates for one supplier** — direct-debit customers get a `Direct debit` label where everyone else gets `Due date`. That settles Q7.6.3 with evidence rather than preference: a supplier owns an *ordered list* of templates, tried until one yields every required field.

#### Where the rule set fails, and what it costs

**Multi-column layouts defeat every line-based rule.** One strata notice interleaves two columns in PDF reading order, so the line after `Total Amount` is a date and the line after `Due Date` is money. No `LinesAfterLabel` can be written for it, and its consistency across samples was 2 in 11 — noise.

This is the case for coordinates, and **it is the first real justification for the `Word { Text; Bottom; Left }` type that `ReadDocumentContent` already returns** (§3.2). Q1.11 kept that type on the grounds that something might need layout; something does. A future `SameRowAsLabel of label` rule — take the words whose vertical position matches the label's and whose horizontal position is to its right — would handle column layouts, and it is the only rule kind here that cannot be written against plain text.

**It is not proposed for the first version.** One supplier in the sample needs it, the existing four kinds plus the three additions cover the rest, and a coordinate rule is materially harder to explain in an editor. It is recorded as the known next rule kind, with a concrete case waiting for it.

Two other categories that look like invoices and are not: **council rates receipts** and **property-management owner statements**. Both parse cleanly and neither carries an invoice reference or a due date, because both are records of money already moved. They are the strongest argument for Q1.5's problem list being visible: the honest answer for these is *"matched a supplier, produced no invoice"*, repeated monthly, and you want to see that and mark it deliberate rather than wonder why nothing appeared.
---

## 4. Proposed change breakdown

One change folder for all of this would be too large to review or to test in the CLAUDE.md sense. Suggested sequence — each is independently shippable and each gets its own `docs/changes/<name>/`:

The answers in §1.1 roughly doubled the original four, and **Q5.4 confirms all seven**, in this order:

| # | Change | Delivers | Depends on |
| --- | --- | --- | --- |
| 1 | `invoice-ledger-foundation` | **The main SQLite database wired into the app for the first time**: `Suppliers` + `SupplierMatchers` migrations, store functions, `SupplierApi`, suppliers page. No invoices, no mail, no calendar | — |
| 2 | `invoice-templates` | The template model (§3.7), `ApplyTemplateWorkflow`, template migrations, templates page with the rule editor and the test panel. Ask #6 | 1 |
| 3 | `thunderbird-account-selection` | Thunderbird accounts page, native folder picker (first host change), recursive `prefs.js` discovery per §3.8, both mbox and maildir, selection persisted. Grew with the Q4.2–Q4.4 answers. Ask #5 | — |
| 4 | `invoice-extraction` | The four document readers (`Integrations.Documents`, absorbing `Integrations.Pdf`), the `Invoices`, `ScanProblems` and `InvoiceTombstones` migrations, the scan pipeline, the invoice table with per-row delete and a problems view — **and the whole scan-window apparatus**: the `ScanWindows` and `InvoiceSettings` migrations with their seed, the settings page, the picker, the remembered choice. **The largest change after #2**, and the one to split first if it gets unwieldy. Ask #2 | 1, 2, 3 |
| 5 | `credentials-per-provider` | **Pure refactor, no new feature.** Deletes `Integrations.Credentials`; each provider integration gains a `Credentials` collection in its own LiteDB. Characterization tests before anything moves. §3.9 | — |
| 6 | `google-account-integration` | Google accounts page, OAuth registration, calendar listing, **and the per-account default invoice calendar** — Q2.3 moved that here from #7. Ask #4 | 5 |
| 7 | `invoice-calendar-sync` | The diff as a four-action plan — create, update, delete, leave alone (Q2.6) — the sync-status column, per-row selection plus a bulk button (Q2.7), `InvoiceCalendarEvents`. **The only change here that can destroy data outside the app**, so §5.18's guard is its headline test. Asks #1 and #3 | 4, 6 |

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
5. **Secrets at rest — deferred on purpose (Q5.6), not overlooked.** The existing credential store persists secrets in plaintext LiteDB, and OAuth refresh tokens are materially worse to leak than what is in there today: a refresh token is durable, silent to use, and grants calendar access until someone revokes it. The decision is to ship without encryption and say so in change #6's description, which is a legitimate call for a single-user desktop app on a machine you control. Two things follow from taking it deliberately: DPAPI (`ProtectedData`, `CurrentUser` scope) remains the low-friction retrofit if that ever changes, and it should be recorded that retrofitting means re-authorising every account, because tokens already written cannot be re-encrypted without being read first.
6. **`Startup.fs` opens its databases at module load.** Three more stores means three more files opened in the working directory on first touch. Fine, but tests must keep away from `Startup` exactly as they do today.
7. ~~**The existing `Documents` area no longer fits.**~~ **Closed by Q1.11** — nothing gives. `ReadDocumentText` (bytes → text lines) is added *beside* `ReadDocumentContent` (path → coordinate-bearing `Word`s), both live in `Documents/`, and `PdfDocumentReader` satisfies both. `ReadDocumentLinesWorkflow`, its contract suite and the `PdfProcessing` scratch project are untouched. The residual cost is honest and small: two dependency types that both mean "read a document", so each needs its own contract suite and the composition root must bind the right one — a `DocumentSource` carrying bytes and a `DocumentPath` are different enough types that it cannot be got wrong silently.
8. ~~**Legacy `.doc` is a materially different problem from `.docx`.**~~ **Closed by Q1.12** — `.docx` only, so NPOI never enters the solution and this stops being a risk. What survives is one line of behaviour: a `.doc` attachment must produce a *listed* unsupported-format problem, not a silent skip. Silence here would look identical to "this supplier sends nothing", and you would never learn the difference.
9. **A user-editable rule engine sits awkwardly with "types carry the rules".** The codebase's instinct is to make invalid states unrepresentable at compile time; a template is typed in at runtime, so the guarantee has to move to a validation boundary — `ValidTemplate`, produced when the page saves and never constructed anywhere else. That's the right answer, but it is a weaker guarantee than the rest of the domain enjoys, and the tests have to carry more of the weight. Add the regex-timeout requirement from §3.7 and this is the riskiest area in the build.
10. **Change #5 deletes tested, working code, and coverage goes down before it goes up.** Removing `Integrations.Credentials` takes three domain workflows, a store, two mappers, a UI page and their tests out of a suite that is currently 204 green tests. That is the correct outcome — code that no longer exists needs no tests — but the change must say plainly what was removed rather than letting the total quietly drop. Characterization tests over the behaviour being *preserved* (a credential round-trips, a secret survives storage unchanged) go in first and are what the new per-provider collections must satisfy.
11. **The main database has never actually been run by the application.** `MigrationSetup` has no caller, there is no composition-root binding, and `createDatabaseContext` opens a `SqliteConnection` it never disposes — which the test guidance already warns keeps temp files locked on Windows. Change 1 is where all of that gets exercised for real for the first time. Expect to find something.
12. **The folder picker forces the first change to the WPF host** — Q4.5 chose the native dialog, so this is now a fact rather than a risk. Blazor cannot supply a filesystem path: `InputFile` hands over content, not locations, and `webkitdirectory` is no better inside a `BlazorWebView`. It means `Microsoft.Win32.OpenFolderDialog` on the WPF side, exposed as an injected `ChooseFolder: unit -> string option` — satisfied by WPF in production and a lambda in tests. A small, clean seam, but it does end the run of changes in which `MainWindow.xaml.cs` never had to be touched. Worth noting in change #3's description rather than slipping it in.
13. **Recursively walking a folder you chose is not the same as reading a known profile path.** It can be enormous, contain several profiles or none, hit directories the process cannot read, and on Windows follow junctions into a loop. The scanner needs a depth bound, permission errors reported per-directory rather than aborting the walk, and a result that distinguishes "no accounts here" from "I could not look". §3.8 adds a concrete case: this profile holds **six orphan mail directories from deleted accounts**, one with 2 GB in it, so "found a mail store" and "found an account" are genuinely different answers.
14. **The mail store is 16 GB, and that is a design input rather than a footnote.** It kills copy-then-parse outright, it makes Q4.8's folder exclusions load-bearing (they remove 9 of 15.2 GB), it forces incremental scanning (Q4.10), and it puts real pressure on Q1.9's rescan-on-every-click. It also strengthens Q5.7: re-deriving invoices from 6 GB of mail on demand is a poor substitute for storing them once. Any performance assumption in this proposal should be checked against that number rather than against a test mailbox.
15. **The clock is a new dependency function type, and CLAUDE.md calls those published interfaces** — meaning `GetCurrentTime` owes a contract suite run against the real implementation *and* every fake. The real implementation is `DateTime.Now`, whose whole nature is to return something different each call, so "assert both sides agree" has no obvious meaning. Workable answer: the suite asserts the properties that must hold of any clock (monotonic across two calls, `Kind` as expected, within a tolerance of `DateTime.Now` at the point of test) and the *cutoff arithmetic* — the part with actual logic — is unit-tested against fixed instants in the workflow. Say this explicitly in the change rather than quietly having no contract test for the clock. Moving the window to **days** removes the trap this bullet used to warn about — `.AddMonths(-1)` from 31 March gives 28 February, and nobody expects that — because days are uniform and `.AddDays(-14.0)` cannot surprise anyone. What replaces it is smaller but real: `DateTime.Now` is local, so a window measured from the exact instant of the click quietly means "N×24 hours ago, at whatever time it is now", and the same window scanned at 09:00 and at 17:00 covers different mail. Anchoring the cutoff to `.Date` fixes that and makes the value stable for the whole day, which incremental scanning (Q4.10) also prefers. **Q1.18.**

16. **Settings are now split across two stores, by rule rather than by accident.** The selected *mail account* lives in the Thunderbird integration's own LiteDB, because §1.1 makes it Thunderbird's own fact. The selected *scan window* lives in the main SQLite database, because you said so and because it is a preference about the invoices page rather than about anyone's mail client. Both placements follow the ownership rule; together they mean there is no single "settings" store to point at, and the next preference will need the same judgement made again. The rule is what belongs in the change description — not the location.
17. **Seeding rows from a migration is a new precedent in this repo.** Every migration so far creates schema and nothing else. The five default windows have to come from somewhere, and the alternatives are worse: `Startup.fs` checking on each launch whether it ought to write five rows is runtime schema management by another name, and hard-coding them in the component is the thing this whole change is undoing. `Insert.IntoTable` in `Up`, matching `Delete.FromTable` in `Down` — but say plainly in change #4 that the file now carries data as well as structure, because the next person will copy whichever migration they open first.
18. **Q2.6 makes the sync destructive, and that is a different class of risk from everything else in this proposal.** Until now the worst this feature could do was write a wrong row to a database it owns, or leave a duplicate calendar event you delete by hand. With `DeleteCalendarEvent` bound, a defect in a *pure* function can remove entries from a calendar the app neither owns nor can restore. Two hazards, both cheap to guard and both unrecoverable if missed: **(a)** deletion driven by *window* absence rather than *ledger* absence — narrowing the picker from 180 days to 7 must never read as "173 days of invoices disappeared"; **(b)** deletion driven by a failed read — if `ListCalendarEvents` comes back empty because a token expired, an unguarded diff concludes every event is missing and its mirror image concludes every invoice is orphaned. **A read failure must abort the plan, never produce one.** Worth a confirmation on any plan containing deletes, at least until change #7 has been used in anger a few times.

19. **On your real mail, only about one invoice in eight states a due date (§3.10).** Every calendar decision in this proposal — Q2.1's all-day event, Q1.10's not-uploadable state, the whole of change #7 — rests on a field that 19% of your invoice PDFs actually contain. Adding `DateFromField (IssueDate, terms)` lifts complete extraction from 12% to 39%, which makes it the highest-value single addition the measurement found; without it, change #7 ships a calendar that stays mostly empty and a ledger that is mostly greyed out. This is not a design fault — it is what the source documents contain — but it should be a known number before #7 is built, not a surprise after.

---

## 6. What would be true when it's done

- `dotnet build MyDogsbody.sln` clean; `dotnet test` green with zero skips, across all four levels — with the one honest exception in §5.2 declared in the change description.
- `MyDogsbody.Domain` still has zero `ProjectReference` elements (`AssertDomainReferencesNothing` + `Contracts/DomainIsolationTests.fs` both still pass).
- Still exactly two mapping points per feature: entity ⇄ domain in each integration, domain ⇄ UI record in `Startup/*ApiMappers.fs`.
- `UI.Portal` references only `UI.Types` and `Exceptions.Types` — one fewer than today, since Q3.8 takes `Enums` out of the graph entirely.
- **Syncing twice in a row makes no API calls the second time.** Not "creates no duplicates" — that was the insert-only bar. With Q2.6's updates in play, a second sync over unchanged data must produce a plan of nothing but `LeaveAlone`, or every run quietly rewrites every event.
- **No `DeleteEvent` is ever produced for an invoice that is merely outside the current window**, and no plan at all is produced from a failed calendar read. Two assertions, both against the pure diff, both cheap — and between them they are what stops this feature from destroying data that isn't ours. §5.18.
- **Scan windows exist as rows and nowhere else.** No list of days in a component, a mapper or a union; the seeded five arrive from a migration and the picker renders whatever the store holds. **Adding a sixth window is done on screen and takes effect without a rebuild** — the same acceptance test as a supplier or a template, one tier smaller.
- **The picker opens on 14 days against a fresh database, on your last choice on every run after that**, and on the fallback if the window you last chose has since been deleted. That third case has a unit test, because it is the one nobody thinks to try by hand.
- Deleting the last remaining scan window is refused with a named error, so the picker can never be empty and no component needs an "if the list is empty" branch.
- No workflow reads the clock directly; every cutoff test pins a fixed instant and asserts an exact date. The same window scanned twice in one day produces the same cutoff both times.
- **Adding a supplier and teaching the app to read its invoices is done entirely on screen** — no rebuild, no F# change, no restart. That is the whole point of ask #6, and it is the acceptance test for it.
- Suppliers, templates and invoices live in the main SQLite database, with the schema built **only** by FluentMigrator — no DDL in a store function, a test, or a SQLite tool.
- A template carrying a pathological regex fails *that rule* with a named error inside its timeout, and the scan finishes.
- **The four worked templates in §3.10 are in the test suite as fixtures**, each asserting every field it claims to extract. They are the only extraction tests in this build written against layouts that demonstrably exist.
- Normalization is applied identically at authoring time and at scan time, and there is a test proving a non-breaking space, a hard-wrapped label and a blank line between label and value all still match. Each of those was found in the real mailbox and each fails silently without it. It does not hang the page.
- `MyDogsbody.Domain` still names no Thunderbird, Google, LiteDB, SQLite, MIME or PDF type — the ledger got bigger, the centre did not get less pure.
- **A scan completes with Thunderbird open**, against both an mbox account and a maildir one, without Thunderbird noticing and without corrupting anything. Reading a live 16 GB mail store is the one operation here that could damage data that isn't ours, so it is read-only by construction — opened for read with `FileShare.ReadWrite`, never copied, never written — and tested that way.
- **The accounts page lists exactly the 10 accounts `prefs.js` declares** for the profile in §3.8 — not the 15 directories under `ImapMail/`, and not the six orphans left by deleted accounts. That number is the acceptance test for discovery.
- **No `Credentials.db`, no `MyDogsbody.Integrations.Credentials`, no `MyDogsbody.Enums`, and no `MyDogsbody.Domain/Credentials/`.** Each provider's credentials sit in that provider's own database, and nothing outside a provider integration opens it. Three projects deleted against one added, so the solution is **two projects smaller** than it is today (§3.9).

---

## 7. Questions to answer

Answer the **blocking** ones before the `requirements.md` for the change they block. The rest can be decided during design, but earlier is cheaper. Each carries my recommendation — "default" is what I'd write if you just said "use your judgement".

**Answered questions are removed from this section** — the decisions they became are recorded in §1.1 and built into §3. **Nothing is left: all six sets are resolved**, 42 questions closed across two rounds and every one of them recorded. Numbering is not contiguous; gaps are answered questions, and numbers were never reused.

| Set | Covers | Blocks change |
| --- | --- | --- |
| ~~§7.1~~ | ~~what an invoice is, and how far back to look~~ — **fully resolved** | ~~4~~ |
| ~~§7.2~~ | ~~what lands on the calendar~~ — **fully resolved** | ~~7~~ |
| ~~§7.3~~ | ~~Google accounts and the credentials removal~~ — **fully resolved** | ~~6, 5~~ |
| ~~§7.4~~ | ~~Thunderbird accounts~~ — **fully resolved**, §3.8 | ~~3~~ |
| ~~§7.5~~ | ~~storage and process~~ — **fully resolved** | ~~4~~ |
| ~~§7.6~~ | ~~templates~~ — **fully resolved**, four of eight from measurement (§3.10) | ~~2~~ |

### 7.1 What an invoice *is*, and how far back to look — ✅ fully resolved

Nothing open. Every question in this set is answered and recorded in §1.1 — including Q1.19, the last one, which this round opened and closed. Two of the answers here closed friction items outright: §5.7 (the `Documents` area) and §5.8 (legacy `.doc`).

### 7.2 What lands on the calendar — ✅ fully resolved

Nothing open. Q2.13 and Q2.14 were opened by the update-and-delete answer earlier in this same round and closed by the end of it. The two that matter most downstream: the sync plan is **visible before it runs** (Q2.13), and an invoice event is **app-owned data that always wins** (Q2.14) — which is the honest cost of Q2.6 and should be said on screen rather than discovered.

### 7.3 Google accounts and the credentials removal — ✅ fully resolved

Nothing open. Q3.1–Q3.6 settle the OAuth flow and Q3.7–Q3.10 settle what the credentials refactor deletes, so **change #5 can be specified now** and **change #6 has no unanswered questions of its own** — it waits only on #5 landing. The decisions are in §1.1 and the demolition list is §3.9.

### 7.4 Thunderbird accounts — ✅ fully resolved

Nothing open. All eleven questions are answered, the plan is measured against the real profile in §3.8, and **change #3 can be specified as soon as you want it.** Alongside change #1 it is one of the two pieces of this build that is ready to write requirements for today.

### 7.5 Storage and process — ✅ fully resolved

Nothing open. Q5.14 — the hole between "you may delete an invoice" and "the scan upserts on a natural key" — is closed with a tombstone, and §3.6 says why that list has to be visible rather than merely present.

### 7.6 Templates — ✅ fully resolved

Nothing open. All eight are recorded in §1.1, and four were answered from measurement rather than judgement — §3.10 tested the model against 1,199 real candidate messages and §3.11 is the rule set that came out of it.

The one to carry into change #2: **Q7.6.6's test panel must display the text after normalization.** Three of the failure modes found in your mailbox are silent non-matches, and a panel showing raw text would cheerfully agree with a template that cannot work.

---

## 8. Next step

**Nothing is open.** 42 questions were answered across this round, every decision is recorded in §1.1, and the two riskiest parts of the build were settled by measuring your actual machine rather than by reasoning: §3.8 against the Thunderbird profile, §3.10 and §3.11 against 1,199 real invoice candidates.

**All seven changes in §4 can now be specified.** The next artefact is a `requirements.md` in a change folder — not another revision of this file.

### Where to start

**Change #1, `invoice-ledger-foundation`.** Small, no external dependency, and it proves the main SQLite database, its migrations and its store shape — which every later change leans on and none of them wants to debug. It is also the only change whose scope did not grow during this round.

#1, #3 and #5 are mutually independent, so any order works. #2 is the largest and now the best understood. #6 waits on #5; #4 on 1/2/3; #7 on 4 and 6.

### The three numbers to carry into implementation

1. **12% → 39%.** Only 12% of your invoice PDFs state a reference, an amount *and* a due date. `DateFromField` plus per-supplier payment terms takes that to 39%. Change #7's value depends on this more than on anything in its own scope — §5.19.
2. **91% vs 8%.** The invoice fields are in the PDF attachment, essentially never in the email body. Build the PDF path first and treat the body reader as the exception it is.
3. **16 GB, 6.2 GB in scope.** Reading is in place with `FileShare.ReadWrite`, never copied, and incremental after the first pass. The header-only scan of 234,446 messages was fast; opening bodies is what costs.

### What this document got wrong, and how it was caught

Worth keeping, because it is the argument for measuring before building:

- **§3.8** overturned structural account detection (would have found 15 accounts where 9 exist) and copy-then-parse (impossible at 16 GB).
- **§3.10** overturned the rule-kind ranking, the value of the email body, the "prefer text/plain" default, and the assumption that no normalization step was needed — that last one silently, since a non-breaking space is enough to make a correct-looking rule never match.
- **Three holes appeared between separately reasonable answers**, none visible from any single one: a transient problem list emptied by incremental scanning (Q1.19), hand-deleted invoices resurrected by upsert (Q5.14), and a pure diff that could delete calendar entries it did not own (§5.18).

Every one of those would have surfaced during implementation instead.

### The one thing still unproven

**No code has been written, and no template has been run by MyDogsbody itself.** §3.11's rule set is derived from real documents and its four worked templates are drawn from layouts that demonstrably exist — but they were exercised by a scratch script, not by `ApplyTemplateWorkflow`. Making those four the first fixtures in change #2's test suite is what closes the gap, and it is the cheapest acceptance test available: the layouts are known, the expected fields are known, and the templates are already written down.

Requirements in EARS notation, agreed before any `design.md`, per CLAUDE.md.
