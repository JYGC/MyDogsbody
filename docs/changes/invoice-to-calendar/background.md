# Background — Invoices from Thunderbird to Google Calendar

**What this is.** The decision record and the measured evidence behind seven change folders. It is
not a change of its own: it has no `requirements.md`, no `design.md` and no `tasks.md`, and nothing
is implemented from it directly. It exists because §1.1's decisions and the two measurement passes
below are **evidence that cannot be re-derived cheaply**, and they belong to no single change.

**Where it came from.** `docs/changes/invoice-to-calendar-pre-proposal.md`, written 2026-08-08 and
last measured 2026-08-09. That file answered all 42 of its own open questions and was then converted
into the seven change folders listed in [The seven changes](#the-seven-changes). It has been deleted;
everything durable from it is here.

**How to use it.** Each change folder's `requirements.md` and `design.md` cite decisions by their
question id (`Q1.7`, `Q7.6.3`) and findings by section (`§3.10 Finding 3`). Those ids resolve here.
When a change disagrees with a decision recorded below, the change says so in its own `design.md`
and states why — this file is the record of what was decided, not a veto over later evidence.

---

## What was asked for

Six user-facing pieces:

1. **An invoice page** with a table listing every invoice found in one account of the Thunderbird
   profile folder, and a button that uploads them to a chosen Google account's calendar.
2. **A scan window on that page** — how far back to scan, in **days**: 7, 14, 30, 90 and 180 to
   begin with, more addable on a settings page. **14 days is the default**, and the choice is
   **saved** so the page opens on it next time. Changing it rescans.
3. **A diff in that same table** — for the selected calendar, which invoices are already on it and
   which are not.
4. **A Google accounts page** for registering Google calendar integrations. Many at once.
5. **A Thunderbird accounts page** listing the accounts in the profile folder, one selectable for
   import.
6. **A supplier templates page** where the templates that dictate how each supplier's mail is
   scanned are maintained. The number of suppliers cannot be known in advance, so templates are
   *data edited at runtime*, not parsers someone writes in F#.

---

## The seven changes

| # | Change folder | Delivers | Depends on |
| --- | --- | --- | --- |
| 1 | `invoice-ledger-foundation` | **The main SQLite database wired into the app for the first time**: `Suppliers` + `SupplierMatchers` migrations, store functions, `SupplierApi`, suppliers page. No invoices, no mail, no calendar | — |
| 2 | `invoice-templates` | The template model, `ApplyTemplateWorkflow`, `MatchSupplierWorkflow`, template migrations, templates page with rule editor and test panel. Ask #6 | 1 |
| 3 | `thunderbird-account-selection` | Thunderbird accounts page, native folder picker (first host change), recursive `prefs.js` discovery, mbox and maildir reading, watermarks, selection persisted. Ask #5 | — |
| 4 | `invoice-extraction` | The four document readers (`Integrations.Documents`, absorbing `Integrations.Pdf`), the `Invoices` / `ScanProblems` / `InvoiceTombstones` migrations, the scan pipeline, the invoice table with per-row delete and a problems view — **and the whole scan-window apparatus**. **The largest change after #2**, and the one to split first if it gets unwieldy. Ask #2 | 1, 2, 3 |
| 5 | `credentials-per-provider` | **Pure refactor, no new feature.** Deletes `Integrations.Credentials`; each provider integration gains a `Credentials` collection in its own LiteDB. Characterization tests before anything moves | — |
| 6 | `google-account-integration` | Google accounts page, OAuth registration, calendar listing, **and the per-account default invoice calendar**. Ask #4 | 5 |
| 7 | `invoice-calendar-sync` | The diff as a four-action plan — create, update, delete, leave alone — the sync-status column, per-row selection plus a bulk button, `InvoiceCalendarEvents`. **The only change that can destroy data outside the app** | 4, 6 |

One change folder for all of this would be too large to review or to test in the CLAUDE.md sense.
**Q5.4 confirmed seven, in this order.**

### Recommended order of execution

> **1 → 2 → 3 → 4 → 5 → 6 → 7** — the order in the table above, which is the order Q5.4 confirmed.
> It satisfies every dependency, respects the one constraint §4 did not record, and puts the largest
> unproven assumption second rather than sixth.

| Step | Change | What you have when it lands | Why here |
| --- | --- | --- | --- |
| 1 | `invoice-ledger-foundation` | Suppliers can be maintained on screen, stored in SQLite | **The one to start with regardless.** Small, no external dependency to negotiate, and it proves the main database, its migrations and its store shape actually work. Every later change leans on that and none of them wants to discover a problem with it (friction #11). It also lands the E2E coverage change #5 needs to exist |
| 2 | `invoice-templates` | Templates authored, edited and run against pasted text in the test panel | **Settles the largest unproven assumption in the whole proposal** — whether the rule set is expressive enough for real suppliers — while it is still cheap to find out. Nothing downstream is worth building if it is not |
| 3 | `thunderbird-account-selection` | Your accounts discovered, one selected, mail readable | Independent of 1 and 2, so it can move; kept here because #4 needs its measured header-pass timing before rescan-on-every-click can be settled |
| 4 | `invoice-extraction` | **A working invoice ledger over your own mail, with no Google involvement at all** | The first genuinely useful milestone, and it covers asks #2, #5 and #6 between them. **Two measurements taken here decide later scope** — see the checkpoints below |
| 5 | `credentials-per-provider` | Two projects lighter; credentials live in their provider's own store | A boring refactor placed deliberately *after* the risky work, so the suite is stable when things start being deleted. It is also the last moment it can happen without blocking #6 |
| 6 | `google-account-integration` | Google accounts registered, each with a default invoice calendar. Ask #4 | Waits only on #5 landing |
| 7 | `invoice-calendar-sync` | The diff, the plan, the sync. Asks #1 and #3 | Last, and the only change that can destroy data outside the application |

**The path 1 → 2 → 3 → 4 gives a working invoice ledger, with your own templates, over your own mail,
with no Google involvement at all.** If you stop there, you have four of the six things that were
asked for.

#### What must not vary

1. **Change #5 must not be first.** It deletes the suite's only E2E file and supplies no replacement
   — see [the sequencing constraint](#one-sequencing-constraint-4-did-not-record) below.
2. **#4 after #1, #2 and #3.** It joins them; it has nothing of its own without all three.
3. **#6 after #5**, and **#7 after #4 and #6.**

#### What may safely vary

- **#3 can move ahead of #2.** It depends on nothing at all. Little is gained, though: #2's test
  panel takes pasted text either way — the real-message picker belongs to #4 — and moving #3 first
  delays the assumption most worth testing early.
- **#5 can go anywhere after #1.** It touches only the credentials path and `Integrations.Google`,
  neither of which #2, #3 or #4 goes near.
- **#4 can be split** if it gets unwieldy. The designated split is the scan-window apparatus; the
  phases in its `tasks.md` are ordered so it lifts out whole.

#### Two checkpoints — stop and look at the numbers

**End of #2 — is the rule set expressive enough?** Do the four measured templates pass as fixtures,
and what due-date coverage do they achieve? This is the cheapest possible moment to find out that
templates need a rule kind nobody has built, because no mail or calendar code exists yet.

**End of #4 — two measurements decide what happens next** (its `tasks.md` phase 12):

- *Scan timing.* If changing the window costs seconds, the immediate rescan is replaced by an
  explicit Refresh button. That was the stated condition Q1.9 was accepted under, and this is where
  it is settled rather than assumed.
- *Real due-date coverage.* **This is the one that could change the plan.** The measurement predicted
  12% → 39% with `DateFromField` and per-supplier payment terms. If the real number stays near 12%,
  change #7 ships a calendar that is mostly empty and a ledger that is mostly greyed out
  (friction #19) — and the higher-value next piece of work is better payment terms or another rule
  kind, not the sync. **Take that number before starting #7, not after.**

#### If more than one person is working

- **#1 alone**, first. Everything else waits on it in practice, even the changes that do not depend
  on it in principle.
- Then **#2 and #3 in parallel.** They share no domain area and no migration timestamps. Expect
  conflicts in the files every change touches — `MyDogsbody.Domain.fsproj`'s compile order,
  `Startup.fs`, `Shell.fs`, `SettingsComponents`, `ActionNames.fs` and `MyDogsbody.Tests.fsproj` —
  all of them append-only and mechanical to merge.
- **#5 alongside either**, once #1 has landed.
- **#4, #6 and #7 are join points** and are best done one at a time.

### Migration timestamps, reserved across the series

So the seven changes stay ordered even if they land out of sequence. Each change's `tasks.md` repeats
its own block.

| Change | Timestamps | Tables |
| --- | --- | --- |
| #1 | `20260809000001`–`…0002` | `Suppliers`, `SupplierMatchers` |
| #2 | `…0003`–`…0004` | `InvoiceTemplates`, `TemplateFieldRules` |
| #4 | `…0005`–`…0009` | `Invoices`, `ScanProblems`, `InvoiceTombstones`, `ScanWindows` (+ seed), `InvoiceSettings` |
| #7 | `…0010` | `InvoiceCalendarEvents` |

### Scope movements recorded during conversion

Boundaries drawn while converting this into change folders, which the pre-proposal left implicit.
Each is restated in the affected change's `design.md` → *Decisions taken*.

- **`MailFolderReader` lands in change #3, not #4.** The integration is complete when it can hand
  over messages and attachments; #4 consumes `ReadMailFolder` as a bound dependency. Follows §3.3,
  which listed discovery, reading, in-place access and watermarks as one integration. It is not dead
  code in #3 — the accounts page's message count runs the same header pass.
- **`ScanCutoff` lives in a new `Domain/MailAccounts/` area, not in `Invoices/`.** A cutoff is a
  parameter *to a mail read*, and the rule it encodes — *skip a message on its `Date` header before
  touching its body* — is the reader's. Putting it in `Invoices/` would make change #3 wait for
  change #2, and §4 requires #3 to be independent.
- **`MatchSupplierWorkflow` lands in change #2, not #4.** It is pure, it is the other half of the
  extraction rule engine, and Q7.6.4 (two suppliers matching one message) is in the template question
  set that blocks #2.
- **`Domain/Invoices/InvoicesTypes.fs` is created in change #2 and extended in #4.** The template
  engine needs `ScannedMessage` and somewhere to put what it pulled out. Same pattern for
  `Calendar/CalendarTypes.fs`, created in #6 (accounts and calendars) and extended in #7 (events and
  the sync plan).
- **The template engine parses as well as extracts, and its output is named `ExtractedInvoice`.**
  §3.2's stage table had a template produce "plain strings" with validation parsing them later. That
  does not survive `ParseHint` being part of the *template*: a parse failure is a diagnostic the test
  panel must show against the rule that caused it, and `DateFromField` cannot derive a due date from
  an unparsed string. A stage type is renamed; no hop is added.
- **`ScannedMessage` carries no `SupplierId`.** `MatchSupplierWorkflow` produces one *from* a scanned
  message, so carrying one would be circular. The supplier lands on the extracted invoice.
- **Only `ListCalendars` is declared in change #6; the four event operations arrive in #7.** A
  dependency function type is a published interface owing a contract suite, and a suite for a type no
  workflow consumes is written against a guess. Same rule kept `ReadDocumentText` out of #2.
- **The invoice table shows three per-row sync states, not four.** Up to date, missing, changed.
  *Orphaned* is not a per-row state because an orphaned event has no invoice row to attach it to; it
  appears in the sync plan and in an orphaned-events view.
- **`FolderPicker` is a UI-level service, not a domain dependency.** §5.12 called for an injected
  `ChooseFolder`; the domain does not open dialogs, so it is registered by the WPF host and consumed
  by the page. One function type, one host registration, one lambda in tests.
- **The Thunderbird accounts page obtains a message count on demand, not on load.** The measurement
  puts a header pass over the real profile at *minutes*; rendering a table cannot cost that. The
  count is a user-triggered action, cached with the time it was taken.

### One sequencing constraint §4 did not record

**Change #5 must not be the first of the seven to land.** It deletes
`E2E/CredentialsFlowTests.fs` — the suite's **only** E2E file today — and supplies no replacement,
because after it there is no credentials page. Landing it first leaves the E2E level empty, which
CLAUDE.md forbids.

§4 records #5 as having no dependencies. That is true of its code and false of its test suite.
**Land change #1 first**, which is the recommended starting point anyway.

---

## Decisions — all 42, answered

Numbering is not contiguous and numbers were never reused; gaps are questions answered in an earlier
round. Every one of these is closed.

### What an invoice is, and how far back to look (§7.1)

| Q | Decision | Consequence |
| --- | --- | --- |
| **Q1.1** where the invoice data lives | **All of them.** PDF, DOC and text attachments, *and* the email body | Extraction is multi-format: one dependency type satisfied by four readers, picked by what the message actually holds |
| **Q1.2** which fields make an invoice | Supplier, invoice number, due date, amount, currency, source message id | "Supplier" is a reference, not a string. Due date turns out to be load-bearing — see Q1.10 |
| **Q1.3 / Q1.4** how invoices are recognised, how many suppliers | **Unknowable in advance.** A user-maintained **template** per supplier says how to scan that supplier's mail | Extraction becomes a small rule engine over user data, plus a page to edit it. The largest single addition to the build |
| **Q1.5** a message that yields no invoice | **Listed with the specific reason. Never silent, never fatal to the scan** | The causes are the diagnostics that tell you which template to fix, so they are a feature of the templates workflow rather than noise |
| **Q1.6** which date the window measures | **The date the mail arrived**, with the picker labelled to say so | The one reading that lets the reader skip a message on its `Date` header before touching its body or attachments — which is what makes a 7-day window cheap over a 16 GB store |
| **Q1.7** which window is selected on first open, and is it remembered | **14 days**, and the choice **persists in the main SQLite database** | Not in the Thunderbird store where the selected account lives. The app's first real user setting, and the first settings table |
| **Q1.9** does changing the window rescan immediately | **Immediate** | Only tenable on the back of Q4.10's incremental scanning. **Change #4 must measure a real scan** before this is treated as settled. If a window change costs seconds, an explicit Refresh comes back |
| **Q1.10** an invoice with no due date | **Stored and listed, greyed out with a reason. Not uploadable** | `UploadableInvoice` is a separate stage type, so the sync workflow cannot be handed one without a due date |
| **Q1.11** how a reader receives an attachment | **Bytes**, via a new `ReadDocumentText`. `ReadDocumentContent` and its coordinate-bearing `Word`s stay | No temp file per attachment per scan, and nothing existing breaks. The two types coexist and `PdfDocumentReader` satisfies both |
| **Q1.12** "DOC" | **`.docx` only** | NPOI never enters the solution. A legacy `.doc` attachment becomes a *listed problem* rather than a silent skip |
| **Q1.13** email bodies | **Both, preferring `text/plain`** — *later corrected by §3.10 Finding 5 to prefer whichever alternative preserves block structure, in practice HTML* | Multipart selection is the reader's job, so a template never has to know which alternative it got |
| **Q1.14** absorb `Integrations.Pdf` | **Yes** — it becomes `MyDogsbody.Integrations.Documents` | One project per *capability* rather than per library, so the composition root binds `ReadDocumentText` once for all four formats |
| **Q1.15** how many invoices | **Assume hundreds** | No server-side paging in the first pass; `MudTable`'s client-side paging is enough |
| **Q1.16** the largest window a user may add | **3650 days**, minimum 1 | A typo guard, not a policy. The bound lives in `ScanWindowDays.create` and nowhere else |
| **Q1.17** what can be done to the window list | **Add and delete, no edit**; seeded rows as deletable as any other; the last one cannot be deleted; deleting the selected one falls back to 14, or to the shortest still present | `CannotDeleteLastScanWindow` as a domain error rather than a UI guard, and `ResolveScanWindowWorkflow` owning the fallback in one place |
| **Q1.18** is the cutoff measured from the start of today | **Start of day** (`getCurrentTime().Date`) | "The last 14 days" names a set of dates rather than 336 hours, so the same window scanned at 09:00 and 17:00 covers the same mail |
| **Q1.19** do scan problems persist | **Yes** — in `ScanProblems`, keyed by source message id, cleared when that message later yields an invoice | Without it, incremental scanning empties the list the moment you rescan and the diagnostic is gone before you look |
| **Scan window values** (supersedes Q1.8) | **Days, not months: 7 / 14 / 30 / 90 / 180 seeded, user can add more** | A window is a **row**, so the set is runtime data. The six-case union dies and the compile-time guarantee becomes a validation boundary |

### What lands on the calendar (§7.2)

| Q | Decision | Consequence |
| --- | --- | --- |
| **Q2.1 / Q2.2** what lands on the calendar | An **all-day event on the due date**; title, description, no reminder | Confirms due date is load-bearing |
| **Q2.3** which calendar | **A default invoice calendar chosen per Google account**, on the Google accounts page | Not a picker on the invoices page. Moves `ListCalendars` into change #6 |
| **Q2.4** what makes the diff "already there" | **A private extended property** on each created event, queried back with `privateExtendedProperty` | Survives renaming and moving. Also means the app only recognises events *it* created |
| **Q2.5** the diff's time window | **The same window as the Thunderbird scan** | One knob, not two — but it cannot be applied literally backwards. The same N is mirrored around today and stretched to cover the latest due date in view |
| **Q2.6** insert-only, or update and delete | **All three** | The diff stops being a status list and becomes a three-action **plan**. Makes the sync **destructive**, which needs a hard guard |
| **Q2.7** upload everything, or a selection | **Both** — per-row checkboxes *and* a bulk button | The button acts on the selection when there is one and on everything outstanding when there isn't |
| **Q2.8 / Q2.11 / Q2.12** partial failure, unset calendar, per-upload override | Continue past a failure and report per row; an account with no default calendar reads as *not ready* with the button disabled; no per-upload calendar override | |
| **Q2.9** does the window bound import, view, or both | **Both**, with the window and count stated above the table | Narrowing hides, it does not forget. That sentence is load-bearing — see hazard (a) below |
| **Q2.10** what goes inside the extended property | **Supplier id + invoice reference**, under one well-known property name, with the local invoice id alongside for diagnostics only | The Q5.8 natural key rather than the database id, so rebuilding the ledger does not make every event read as missing |
| **Q2.13** what makes an event eligible for deletion | **The invoice left the ledger**, and the delete happens **on the next sync**, visible in the plan before you press it | A plan you can see before it runs is the difference between trusting this feature with delete permission and not |
| **Q2.14** does a sync overwrite a hand-edited event | **Yes — the event is app-owned and always wins.** Title and date are rewritten | The honest cost of Q2.6. Justified because a title disagreeing with the ledger is worse than a lost edit — but the page should not pretend otherwise |

### Google accounts and the credentials removal (§7.3)

| Q | Decision | Consequence |
| --- | --- | --- |
| **Q3.1 / Q3.2 / Q3.5 / Q3.6** the OAuth flow | System browser + loopback via `GoogleWebAuthorizationBroker`; one app-wide client secret pasted once; the `userinfo.email` scope so accounts show their address; removing an account deletes the local token and does not revoke at Google | As the `GoogleCalendarCRUD` prototype already does |
| **Q3.3 / Q3.4** where credentials live | **Remove the separate credentials integration.** Credentials go in a `Credentials` collection inside **the provider integration's own LiteDB** | The only decision that deletes existing, working, tested code |
| **Q3.7** what happens to `MyDogsbody.Domain/Credentials/` | **It stops being a domain area** | Nothing in the domain ever reasoned about a credential. Deletes `CredentialsTypes.fs` and three green workflows with their tests — a real coverage loss to state plainly |
| **Q3.8** do `InfrastructureType` and `Infrastructure` go too | **Both, and `MyDogsbody.Enums` with them** | The database identifies the provider, so the discriminator was a second source of truth. Removes a pair of edge mappers and drops `Enums` out of `UI.Portal`'s reference set |
| **Q3.9** the rows already in `Credentials.db` | **Discard** | No migration. A development database in `bin\Debug\net9.0\` holding a handful of retypeable rows |
| **Q3.10** does `/settings/credentials` disappear | **Yes, entirely** | With `CredentialsComponents`, `CredentialsBrowserModule*` and their tests. The Google accounts page *is* Google's credential page |

### Thunderbird accounts (§7.4)

| Q | Decision | Consequence |
| --- | --- | --- |
| **Q4.1** what "accounts" means | **The mail accounts configured in Thunderbird**, one selected at a time | No folder-level selection |
| **Q4.2** how the profile is located | **You give a folder, and it is searched recursively** | Not `profiles.ini` discovery. Needs a folder picker |
| **Q4.3** mbox or maildir | **Both** | No "mbox first" phasing; format detection becomes real work |
| **Q4.4** is Thunderbird running | **Yes** | The store must be read without a clean lock |
| **Q4.5** how the folder is chosen | **Native folder dialog** | `Microsoft.Win32.OpenFolderDialog` on the WPF side, injected as a `ChooseFolder` function. First change to touch the host |
| **Q4.6 / Q4.7** how accounts are found, and may we read in place | **Settled by measuring the real profile** — see [The Thunderbird profile, measured](#the-thunderbird-profile-measured) | `prefs.js` is the mechanism, not a fallback; and at **16 GB**, copy-then-parse is impossible |
| **Q4.8** which folders are scanned | Excludes Trash / Deleted / Junk / Sent / Drafts | 6.2 GB in scope, not 15.2 |
| **Q4.9** duplicate profiles | Listed, qualified by path | |
| **Q4.10** incremental scanning | Per-folder watermarks with a full-rescan escape | |
| **Q4.11** maildir | Built against synthetic fixtures only | The real profile is 100% mbox |

### Storage and process (§7.5)

| Q | Decision | Consequence |
| --- | --- | --- |
| **Q5.1** which storage tier | **Invoices are MyDogsbody items, not Integration items.** So are **suppliers** | The main SQLite database stops being theoretical |
| **Q5.2 / Q5.3 / Q5.5** testing and housekeeping | Google contract suites run against fakes + stubbed HTTP with live verification recorded as manual; synthetic mbox and invoice fixtures committed; `GoogleCalendarCRUD` deleted in change #6 | |
| **Q5.4** is the seven-change breakdown right | **Seven, in the order given** | The four-change alternative is off the table |
| **Q5.6** encrypt OAuth tokens at rest | **Deferred, explicitly** | An accepted risk, not an oversight — written into change #6's description so it is a decision on the record |
| **Q5.7** do invoices really persist | **Yes.** The ledger is real | An invoice is a stored fact, not a view recomputed from your mailbox |
| **Q5.8** what makes two scans agree | **Supplier + invoice reference** as the natural key, `SourceMessageId` for traceability, unique index in the migration | Rescanning an overlapping window updates rather than duplicates, and the database refuses a duplicate even if the code is wrong |
| **Q5.9** where SQLite store functions live | **In `MyDogsbody.Database`**, which gains a `ProjectReference` to `MyDogsbody.Domain` | No new project. Outer-ring shape preserved |
| **Q5.10** template export/import | **Not in the first pass**, but the model stays serialisable | Already true: templates are relational rows, so an export is a read and a write |
| **Q5.11** how much provenance an invoice keeps | **`SourceMessageId` and nothing else** | Thunderbird's vocabulary stops at the integration boundary |
| **Q5.12** can an invoice be corrected or deleted by hand | **Delete yes, edit no** | A `DeleteInvoice` workflow and a per-row action |
| **Q5.13** typed settings table or key/value | **Typed** | `InvoiceSettings`, one row, a column per setting, a migration to add one |
| **Q5.14** does a hand-deleted invoice stay deleted | **Yes** — a **tombstone** on the natural key, which the scan skips. Visible and reversible | Without it "delete" meant "hide until the next scan" |

### Templates (§7.6)

| Q | Decision | Consequence |
| --- | --- | --- |
| **Q7.6.1** which rule kinds | **The seven in [The rule set](#the-rule-set)**, not the original four | `LinesAfterLabel` is the workhorse; `SubjectCapture`, `AttachmentName` and `DateFromField` were each added on measured evidence |
| **Q7.6.2** raw regex on the page | **Keep it**, with the mandatory match timeout | It fired exactly once in 1,199 candidates, so it is a genuine escape hatch. Its editor is the **last** one built in change #2 |
| **Q7.6.3** several templates per supplier | **Filter by document part, try in your order, first complete match wins**, and record which template produced each invoice | Settled by data: one water utility labels the same field `Due date` for most customers and `Direct debit` for direct-debit ones |
| **Q7.6.4** two suppliers match one message | **An error against that message**, shown in the table | Silently picking one is how a month of invoices ends up filed under the wrong supplier |
| **Q7.6.5** how a supplier is matched | **Sender address, sender domain and subject pattern; several per supplier; matching on any** | Sender domain does most of the work. The matcher stays on the supplier, not the template |
| **Q7.6.6** is the test panel in scope | **Yes**, and it must show the text **after normalization** | Three of the failure modes below are silent non-matches indistinguishable from "this supplier sends nothing" |
| **Q7.6.7** a template changes, what happens to invoices it made | **Leave them**; the next scan of a covering window updates them via the Q5.8 key | A reprocess button is a follow-up, and Q1.19 makes it cheap when it comes |
| **Q7.6.8** currency | **`FixedValue` per template**, overridable by a rule | 96% of documents carry `$` and every one sampled is AUD |

---

## The ownership rule — "MyDogsbody items, not Integration items"

Invoices and suppliers are the application's own concepts with their own lifecycles; the integrations
are only where they came from and where they are pushed to. Concretely:

- Types live in `MyDogsbody.Domain/Invoices/` and `MyDogsbody.Domain/Suppliers/` and **name nothing**
  from Thunderbird, Google, mbox or MIME.
- **Supplier is an entity, not a string field.** An invoice carries a `SupplierId`; the supplier
  record carries the name. That is what makes "every invoice from this supplier" answerable, and what
  a template hangs off. A free-text supplier name on each invoice gives you three spellings of the
  same company and no way to attach a template to any of them.
- **Suppliers, templates and invoices persist in the main SQLite database.** CLAUDE-project.md
  reserves the per-integration LiteDB stores for *an integration's own* data, and none of these are
  that. This feature is the **first consumer of `MyDogsbody.Database`**.
- `Integrations.Thunderbird` owns only Thunderbird's own facts: the profile path, the discovered
  accounts, the folder lists, the scan watermarks, which account is selected. It hands over messages
  and attachments and does not store, define or number invoices. **None of it reaches the main
  SQLite database.**
- `Integrations.Google` owns only Google's own facts: registered accounts, their default invoice
  calendar, and their **credentials**, in a `Credentials` collection in its own database.
- **An invoice outlives both.** Removing the Google account, or switching the Thunderbird account,
  does not delete invoices or suppliers.
- Reading a PDF is still an *integration* — an adapter for a capability the domain declares. The
  reading is infrastructure; the invoice is not.

**A Thunderbird account is not a MyDogsbody item**: it is a fact about someone else's mail client,
discovered by reading their files, meaningless if you uninstall Thunderbird. An *invoice* extracted
from it is a MyDogsbody item and survives. Those two sentences draw the line for every future
question of where something belongs.

### Settings are split across two stores, by rule rather than by accident

The selected **mail account** lives in the Thunderbird integration's LiteDB, because it is
Thunderbird's own fact. The selected **scan window** lives in the main SQLite database, because it is
a preference about the invoices page rather than about anyone's mail client. Both follow the
ownership rule; together they mean there is no single "settings" store to point at, and the next
preference needs the same judgement made again. **The rule is what carries forward, not the
location.**

---

## The Thunderbird profile, measured

Measured against `C:\Users\jygcn\AppData\Roaming\Thunderbird\Profiles\49stkd1y.default` on
2026-08-08. Everything below is what that profile actually contains, not what the format
documentation says it should.

| | |
| --- | --- |
| Accounts configured | **10** — 9 IMAP + Local Folders |
| Directories under `ImapMail/` | **15** |
| **Orphan directories** from deleted accounts | **6**, one holding a 2 GB `Deleted` file |
| Store format | **All 10 `berkeleystore` (mbox). Zero maildir** |
| Total mail store | **16 GB** (`ImapMail` 16 GB, `Mail` 267 MB) |
| Largest single mbox file | **2.5 GB** (`imap.googlemail-1.com/[Gmail].sbd/Trash`) |
| mbox files in total | **599** |
| In `Trash`/`Deleted`/`Junk`/`Sent`/`Drafts` | **9.0 GB** |
| Everything else | **6.2 GB** |

### Three findings that changed the design

**1. `prefs.js` is the mechanism, not a fallback.** Structural detection would find 15 IMAP
"accounts" where 9 exist — a 60% false-positive rate — because deleted accounts leave their
directories behind, one with 2 GB still in it. Worse, directory names are disambiguated with a
numeric infix (`imap.googlemail.com`, `imap.googlemail-1.com`, `-2`, `-3` are four different Google
accounts on one host), so a directory name is neither the hostname nor the account. **Discovery
reads `prefs.js`. A directory with no account pointing at it is ignored.**

**2. `mail.server.serverN.directory` is stale; use `directory-rel`.** In this profile the absolute
path records `C:\Users\JunYing\...` while the profile lives at `C:\Users\jygcn\...` — the Windows
account was renamed and Thunderbird never rewrote it:

```
mail.server.server2.directory     = C:\Users\JunYing\...\Mail\Local Folders   ← wrong
mail.server.server2.directory-rel = [ProfD]Mail/Local Folders                 ← right
```

Resolve `[ProfD]` against **the folder the user chose**, never against the recorded absolute path.
This is also what makes a copied or relocated profile work at all.

**3. At 16 GB, copy-then-parse is impossible.** A single 2.5 GB mbox cannot be copied per scan, let
alone 16 GB. The reader **must** open with `FileShare.ReadWrite` and read in place, tolerating a torn
final message. There is no fallback to argue about.

It also made **the folder exclusions load-bearing rather than tidy** — dropping Trash, Deleted, Junk,
Sent and Drafts removes 9.0 GB of 15.2 GB, the difference between a scan that is feasible and one
that is not. And it forces **incremental scanning**: re-reading 6.2 GB every time the window picker
changes is not viable, so each folder needs a watermark (file size and mtime at last scan, plus the
offset reached) and a scan must read only what has been appended since. mbox is append-only in normal
operation, which is what makes this sound; a compact or a repair resets the watermark and forces a
full re-read of that folder.

### The discovery algorithm

Given the folder the user chose:

1. **Walk recursively for `prefs.js`.** Each one found is a profile root; `[ProfD]` for its accounts
   resolves to the directory containing it. Handles the chosen folder being one profile, a parent of
   several, or a backup.
2. **Read `mail.accountmanager.accounts`** — an ordered CSV of account keys, and the authoritative
   list. In this profile it holds 10 keys while `mail.account.lastKey` is 20 and the numbering has
   gaps (no `account4`, `5`, `7`, `8`, `11`–`16`). **Never iterate `1..lastKey`.**
3. **For each account key**, read `mail.account.<key>.server` → `serverN`, and
   `mail.account.<key>.identities` → a CSV of identity keys (`account18` here has two: `id10,id11`).
4. **For each `serverN`**, read `type` (`imap`, `pop3`, `none` for Local Folders), `hostname`,
   `userName`, `name` (the display name — usually the email address), `storeContractID`
   (`berkeleystore` = mbox, `maildirstore` = maildir) and `directory-rel`.
5. **For each identity**, read `mail.identity.<id>.useremail` and `.fullName`. That is the address a
   supplier matcher compares against, and an account can have more than one.
6. **Resolve the store directory** from `directory-rel`, and confirm it exists. If it does not,
   report the account as configured-but-missing rather than dropping it silently.
7. **Enumerate folders** inside the store: an extensionless file is a folder's mbox, a sibling `.sbd`
   directory holds its children, and nesting repeats to arbitrary depth
   (`Music.sbd/Surrey Hills Orchestra.sbd/Messages` exists here). **Ignore `.msf` entirely** — it is
   Mork, and it is not a reliable index of what exists: this profile has `Archives.msf` and
   `Drafts.msf` with no corresponding mbox file at all.

Steps 1–7 are pure parsing over text files and a directory listing. They are fast, they touch no
mail, and they are exactly what the mail accounts page needs to render its table — which means the
page can be built and tested before a single message is read.

---

## The mailbox, measured

Measured 2026-08-09 against the same profile. **Everything below is a count, not a guess.** No
invoice contents are reproduced — supplier names and layout shapes only. Amounts, references, account
numbers and addresses stay out of version control on purpose.

| | |
| --- | --- |
| In-scope mbox files walked | **505** (Trash / Junk / Deleted / Sent / Drafts excluded) |
| Messages whose headers were parsed | **234,446** |
| Invoice-like candidates (subject or sender) | **1,199** |
| PDF attachments extracted and read | **644** |
| Suppliers appearing more than twice | **~30** |

The header pass over 234,446 messages ran in minutes on a cold cache — the first practical
confirmation that immediate rescan (Q1.9) and watermarks (Q4.10) are on solid ground: **skipping on
headers is cheap, and opening bodies is what costs.**

### Finding 1 — the data is in the PDF, and almost never in the body

Per document that contained readable text:

| Field found by any label rule | In PDF attachments (n=558) | In email bodies (n=1,164) |
| --- | --- | --- |
| Invoice reference | **91%** | 8% |
| Amount | 77% | 8% |
| Due date | 22% | 2% |

**The body is not a secondary source, it is a rounding error.** `Attachment Pdf` does essentially all
the work. The two largest suppliers by volume both send a body that says, in full, some variant of
*"attached is your invoice"*.

`.pdf` is effectively the only attachment format that matters: **644 PDFs, 114 `.xlsx`, 1 `.docx`,
0 legacy `.doc`**. Q1.12 chose `.docx`-only and the mailbox says even that is close to dead — while
spreadsheets, which no reader is planned for, outnumber Word documents 114 to 1. None of the `.xlsx`
files sampled were invoices (they are property-management statements), so this is not a call to build
a fourth reader; it is a reason to make the unsupported-format problem row say **which** format, so
the question can be answered from data later.

### Finding 2 — PDF text extraction works, and needs no OCR

| | |
| --- | --- |
| PDFs with an extractable text layer | **610 / 644 — 94.7%** |
| PDFs with no text layer (scanned images) | **10 — 1.6%** |
| PDFs that failed to open at all | **24 — 3.7%** |
| Page counts | single-page 441 · two-page 150 · three or more 51 |

**No OCR is required**, which removes the largest cost risk in the extraction path. The 34 unreadable
PDFs are a `ScanProblem` row each, not a reason to add Tesseract.

### Finding 3 — the due date is the binding constraint, and it is worse than expected

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

**Only about one invoice in eight states a due date.** An all-day event on the due date (Q2.1) plus
a due-date-less invoice stored as *not uploadable* (Q1.10) means that, as specified, roughly seven
invoices in eight arrive in the ledger greyed out and the calendar stays nearly empty. That is not a
defect in the design; it is what the source data contains.

**The fix is one rule kind.** 48% of documents carry an issue or invoice date, and payment terms are
a property of the supplier rather than of the document. Deriving the due date from the issue date
plus the supplier's terms lifts complete coverage from **12% to 39%** — a 3.2× improvement for one
rule and one number per supplier. It is the single highest-value change this scan suggests.

### Finding 4 — normalization is mandatory, and its absence is invisible

Three separate ways the text arrives unusable, each of which silently produces "rule matched nothing"
rather than an error:

1. **Non-breaking spaces.** The largest supplier's PDF renders as `Invoice\u00a0Number:\u00a07422`.
   A rule written as `AfterLabel "Invoice Number:"` never matches, and nothing on screen explains why.
2. **Hard-wrapped labels.** One utility's email wraps `Invoice number` across two lines and splits
   the value across two more — in *both* the plain-text and HTML alternatives, because the wrapping
   is in the source. Label-anchored rules cannot see a label that is not on one line.
3. **Letter-spaced headings.** PDF text extraction returns `TA X INVOICE` where the document shows
   `TAX INVOICE`, because the letters are individually positioned.

So the rule engine needs a **defined normalization contract, applied before any rule runs** and
identical at authoring time and scan time:

- Unicode NFKC, then fold `U+00A0`, `U+2007`, `U+202F` and friends to a plain space.
- Collapse runs of spaces and tabs; strip leading and trailing space per line.
- Join a wrapped continuation line to its predecessor **within a block**, where a block is a table
  cell or paragraph — never across blocks, or `LinesAfterLabel` loses the structure it depends on.
- Drop empty lines before applying line offsets, so `LinesAfterLabel(label, 1)` means "the next line
  with content".

Every one of the three would otherwise produce a template that worked in the test panel and failed in
production, or vice versa.

### Finding 5 — "prefer text/plain" is wrong for this mail

589 of 1,199 candidates carry both a `text/plain` and a `text/html` alternative. Where the body
matters at all, the HTML is the **better** source, because the table structure keeps a label and its
value adjacent while the plain-text alternative has already thrown that away by wrapping. Given
Finding 1 this affects few invoices, so it is a correction rather than a crisis — but Q1.13's
reasoning does not survive contact with the data. **The answer is "prefer whichever alternative
preserves block structure", which in practice means HTML.**

---

## The rule set

What the measurements say the template DSL should be.

### Kept, and validated by the data

| Rule | Evidence |
| --- | --- |
| `LinesAfterLabel of label * offset` | **The dominant kind.** Yarra Valley Water, Xero and OC Energy all print the label on its own line with the value beneath. Offset is almost always 1 |
| `AfterLabel of label` | IODM's whole invoice is `Label: value`; Xero uses it for the due date in the same document where the reference is label-above-value |
| `RegexCapture of pattern` | Earns its place as the escape hatch — one supplier's body is a single sentence carrying both reference and amount, which no label rule can decompose |
| `FixedValue` | 96% of documents carry `$` and every one sampled is AUD |

**A template must allow a different rule kind per field.** Xero proves it: label-above-value for the
reference and inline `Label: value` for the due date, in one document. Each `TemplateFieldRule`
carries its own `Rule`, so this is a requirement rather than an accident.

### Added, each for a measured reason

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

- **`SubjectCapture`** — **209 of 1,199 candidates (17%) carry the invoice reference in the subject
  line**, and the original model had no way to read it. One supplier's subject states the reference
  explicitly while its PDF states it only in a table.
- **`AttachmentName`** — the largest supplier by volume names every attachment for its invoice number
  and nothing else; a utility names the file with the same reference its PDF prints. **The most
  reliable single field source found in the whole scan**, and it costs nothing to read.
- **`DateFromField`** — the 12% → 39% rule. `DateFromField IssueDate` means *due this supplier's
  payment terms after the issue date the template already extracted*. Needs `IssueDate` added to
  `TargetField`, which is worth having regardless: 48% of documents state one.

  **The term length lives on the supplier, not on the rule** — `StoredSupplier.PaymentTermDays`.
  "Acme bills net 30" is a fact about Acme in exactly the way "is this mail from Acme?" is. Putting
  the number on the rule would let one supplier's two templates disagree about when its own invoices
  fall due.

`TargetField` is `Reference | Amount | Currency | IssueDate | DueDate`.

### Two hazards the DSL must handle, not the template author

1. **Ambiguous numeric dates.** 26% of dates are `d/m/yyyy` and 5% are `d/m/yy`. On Australian mail
   `02/08/2016` is 2 August; read as US month-first it becomes 8 February — **a six-month error that
   silently lands an event on the wrong day**. `ParseHint.AsDate` must take an explicit format string
   and never fall back to `DateTime.Parse` with ambient culture. Dominant formats, in order:
   `d MMM yyyy` (63%), `d/M/yyyy` (26%), `d/M/yy` (5%), `MMM d, yyyy` (3%).
2. **References printed with grouping spaces.** One utility prints its invoice number in three
   space-separated groups while naming the attachment with the same digits unspaced. Under Q5.8 those
   are two different natural keys for one invoice, so **`InvoiceReference.create` must normalize
   internal whitespace** — otherwise the same invoice read from the PDF and from the filename
   produces two ledger rows and two calendar events.

### The four worked templates

Shapes only, values redacted. These are the four suppliers whose layouts proved consistent across
every sample, and **they are the first fixtures change #2's test suite must carry.**

| Supplier | Part | Reference | Amount | Dates |
| --- | --- | --- | --- | --- |
| Invoice-management platform (271 msgs) | `Attachment Pdf` | `AfterLabel "Invoice Number:"` — or `AttachmentName "^(\d+)\.pdf$"`, equally reliable and cheaper | `LinesAfterLabel ("Total", 1)` | `AfterLabel "Date:"` as **issue** date; no due date is ever printed, so `DateFromField IssueDate` |
| Water utility (52 msgs) | `Attachment Pdf` | `LinesAfterLabel ("Invoice number", 1)` | `LinesAfterLabel ("Amount due", 1)` | `LinesAfterLabel ("Due date", 1)`, **and a second template** using `"Direct debit"` |
| Accounting-platform invoices (15 msgs) | `Attachment Pdf` | `LinesAfterLabel ("Invoice Number", 1)`, or `SubjectCapture` | `LinesAfterLabel ("Amount AUD", 1)` | `AfterLabel "Due Date"` inline; `LinesAfterLabel ("Invoice Date", 1)` |
| Embedded-network energy retailer (15 msgs) | `Attachment Pdf` | `LinesAfterLabel ("Invoice Number", 1)` | `LinesAfterLabel ("Total Amount Due", 1)` | `LinesAfterLabel ("Due Date", 1)` and `"Date of Issue"` |

**The water utility needs two templates for one supplier** — direct-debit customers get a
`Direct debit` label where everyone else gets `Due date`. That settles Q7.6.3 with evidence rather
than preference: a supplier owns an *ordered list* of templates, tried until one yields every
required field.

### Where the rule set fails, and what it costs

**Multi-column layouts defeat every line-based rule.** One strata notice interleaves two columns in
PDF reading order, so the line after `Total Amount` is a date and the line after `Due Date` is money.
No `LinesAfterLabel` can be written for it, and its consistency across samples was 2 in 11 — noise.

This is the case for coordinates, and **it is the first real justification for the
`Word { Text; Bottom; Left }` type that `ReadDocumentContent` already returns.** Q1.11 kept that type
on the grounds that something might need layout; something does. A future `SameRowAsLabel of label`
rule — take the words whose vertical position matches the label's and whose horizontal position is to
its right — would handle column layouts, and it is the only rule kind here that cannot be written
against plain text.

**It is not proposed for the first version.** One supplier in the sample needs it, the existing four
kinds plus the three additions cover the rest, and a coordinate rule is materially harder to explain
in an editor. **It is recorded as the known next rule kind, with a concrete case waiting for it.**

Two other categories that look like invoices and are not: **council rates receipts** and
**property-management owner statements**. Both parse cleanly and neither carries an invoice reference
or a due date, because both are records of money already moved. They are the strongest argument for
the problem list being visible: the honest answer for these is *"matched a supplier, produced no
invoice"*, repeated monthly, and you want to see that and mark it deliberate rather than wonder why
nothing appeared.

---

## Where this rubs against the current architecture

Nineteen friction points, each carried into the change that has to deal with it. The **Change**
column says which `design.md` owns the resolution.

| # | Friction | Change |
| --- | --- | --- |
| 1 | **The Google client is async; domain workflows are synchronous `Result`.** The prototype uses `.Result`, which blocks. Either keep blocking (calls already run off the render thread via `startWork`) or take **FsToolkit.ErrorHandling** for `asyncResult`. **Recommendation: blocking in #6, revisit if #7's batch makes the UI feel stuck** | 6, 7 |
| 2 | **Contract tests against a network service.** For `CreateCalendarEvent` the real adapter is Google. Run the shared suite against the fakes plus a stubbed `HttpMessageHandler`, and **record live verification as manual coverage in the change description** — stated explicitly rather than quietly skipped | 6, 7 |
| 3 | **Thunderbird files may be locked or mid-write.** Reading mbox under a live lock is a genuine failure mode, not an edge case — it needs a named error case and a sentence on screen | 3 |
| 4 | **`.msf` index files are Mork format** — do not parse them. Either read the mail store directly, or read `global-messages-db.sqlite` (gloda), which is SQLite but is not guaranteed enabled or current | 3 |
| 5 | **Secrets at rest — deferred on purpose (Q5.6), not overlooked.** An OAuth refresh token is durable, silent to use, and grants calendar access until revoked. Shipping without encryption is a legitimate call for a single-user desktop app on a machine you control. Two things follow: DPAPI (`ProtectedData`, `CurrentUser`) is the low-friction retrofit, and **retrofitting means re-authorising every account**, because tokens already written cannot be re-encrypted without being read first | 5, 6 |
| 6 | **`Startup.fs` opens its databases at module load.** Three more stores means three more files opened in the working directory on first touch. Fine, but tests must keep away from `Startup` exactly as they do today | 1, 3, 6 |
| 7 | ~~The existing `Documents` area no longer fits~~ — **closed by Q1.11.** `ReadDocumentText` (bytes → text lines) is added *beside* `ReadDocumentContent` (path → coordinate-bearing `Word`s); `PdfDocumentReader` satisfies both. Residual cost: two dependency types that both mean "read a document", so each needs its own contract suite | 4 |
| 8 | ~~Legacy `.doc` is a materially different problem from `.docx`~~ — **closed by Q1.12.** What survives is one line of behaviour: a `.doc` attachment must produce a *listed* unsupported-format problem, not a silent skip. Silence looks identical to "this supplier sends nothing" | 4 |
| 9 | **A user-editable rule engine sits awkwardly with "types carry the rules".** A template is typed in at runtime, so the guarantee moves to a validation boundary — `ValidTemplate`, produced when the page saves and never constructed anywhere else. That is the right answer, but it is a **weaker guarantee than the rest of the domain enjoys**, and the tests carry more of the weight. With the regex-timeout requirement, this is the riskiest area in the build | 2 |
| 10 | **Change #5 deletes tested, working code, and coverage goes down before it goes up.** Three domain workflows, a store, two mappers, a UI page and their tests leave a suite that is currently 204 green. That is correct — code that no longer exists needs no tests — but **the change must say plainly what was removed rather than letting the total quietly drop** | 5 |
| 11 | **The main database has never actually been run by the application.** `MigrationSetup` has no caller, there is no composition-root binding, and `createDatabaseContext` opens a `SqliteConnection` it never disposes. **Change 1 exercises all of it for the first time. Expect to find something** | 1 |
| 12 | **The folder picker forces the first change to the WPF host.** Blazor cannot supply a filesystem path: `InputFile` hands over content, not locations, and `webkitdirectory` is no better inside a `BlazorWebView`. `Microsoft.Win32.OpenFolderDialog` on the WPF side, exposed as an injected `ChooseFolder: unit -> string option`. **It ends the run of changes in which `MainWindow.xaml.cs` never had to be touched** | 3 |
| 13 | **Recursively walking a folder you chose is not the same as reading a known profile path.** It can be enormous, contain several profiles or none, hit unreadable directories, and on Windows follow junctions into a loop. Needs a depth bound, per-directory permission errors rather than an aborted walk, and a result that distinguishes "no accounts here" from "I could not look". The real profile holds **six orphan mail directories**, so "found a mail store" and "found an account" are genuinely different answers | 3 |
| 14 | **The mail store is 16 GB, and that is a design input rather than a footnote.** It kills copy-then-parse, makes the folder exclusions load-bearing, forces incremental scanning, and puts real pressure on Q1.9's rescan-on-every-click. **Any performance assumption should be checked against that number rather than against a test mailbox** | 3, 4 |
| 15 | **The clock is a new dependency function type, and CLAUDE.md calls those published interfaces** — so `GetCurrentTime` owes a contract suite run against the real implementation *and* every fake. The real implementation is `DateTime.Now`, whose whole nature is to return something different each call. **Workable answer:** the suite asserts the properties any clock must have (monotonic across two calls, expected `Kind`, within tolerance of `DateTime.Now` at test time) and the *cutoff arithmetic* is unit-tested against fixed instants in the workflow. **Say this explicitly rather than quietly having no contract test for the clock** | 4 |
| 16 | **Settings are split across two stores, by rule rather than by accident** — see [the ownership rule](#settings-are-split-across-two-stores-by-rule-rather-than-by-accident) | 3, 4 |
| 17 | **Seeding rows from a migration is a new precedent in this repo.** Every migration so far creates schema and nothing else. `Insert.IntoTable` in `Up`, matching `Delete.FromTable` in `Down` — but **say plainly that the file now carries data as well as structure, because the next person will copy whichever migration they open first** | 4 |
| 18 | **Q2.6 makes the sync destructive, and that is a different class of risk.** With `DeleteCalendarEvent` bound, a defect in a *pure* function can remove entries from a calendar the app neither owns nor can restore. **Two hazards, both cheap to guard and both unrecoverable if missed:** **(a)** deletion driven by *window* absence rather than *ledger* absence — narrowing the picker from 180 days to 7 must never read as "173 days of invoices disappeared"; **(b)** deletion driven by a failed read — if `ListCalendarEvents` comes back empty because a token expired, an unguarded diff concludes every event is missing and its mirror image concludes every invoice is orphaned. **A read failure must abort the plan, never produce one** | 7 |
| 19 | **Only about one invoice in eight states a due date.** Every calendar decision rests on a field 19% of the invoice PDFs actually contain. `DateFromField` plus per-supplier payment terms takes complete extraction from 12% to 39%. **Without it, change #7 ships a calendar that stays mostly empty and a ledger that is mostly greyed out.** This is not a design fault — it is what the source documents contain — but it should be a known number before #7 is built | 2, 7 |

---

## What is true when all seven have landed

The acceptance criteria for the feature as a whole. Each change's `requirements.md` carries the
subset it owns.

- `dotnet build MyDogsbody.sln` clean; `dotnet test` green with zero skips, across all four levels —
  with the one honest exception in friction #2 declared in the change description.
- `MyDogsbody.Domain` still has zero `ProjectReference` elements (`AssertDomainReferencesNothing` and
  `Contracts/DomainIsolationTests.fs` both still pass).
- Still exactly **two mapping points per feature**: entity ⇄ domain in each integration,
  domain ⇄ UI record in `Startup/*ApiMappers.fs`.
- `UI.Portal` references only `UI.Types` and `Exceptions.Types` — one fewer than today, since Q3.8
  takes `Enums` out of the graph entirely.
- **Syncing twice in a row makes no API calls the second time.** Not "creates no duplicates" — that
  was the insert-only bar. With updates in play, a second sync over unchanged data must produce a
  plan of nothing but `LeaveAlone`, or every run quietly rewrites every event.
- **No `DeleteEvent` is ever produced for an invoice that is merely outside the current window**, and
  **no plan at all is produced from a failed calendar read.** Two assertions, both against the pure
  diff, both cheap — and between them they are what stops this feature from destroying data that
  isn't ours.
- **Scan windows exist as rows and nowhere else.** No list of days in a component, a mapper or a
  union; the seeded five arrive from a migration and the picker renders whatever the store holds.
  **Adding a sixth window is done on screen and takes effect without a rebuild.**
- **The picker opens on 14 days against a fresh database, on your last choice on every run after
  that**, and on the fallback if the window you last chose has since been deleted. That third case
  has a unit test, because it is the one nobody thinks to try by hand.
- Deleting the last remaining scan window is refused with a named error, so the picker can never be
  empty and no component needs an "if the list is empty" branch.
- No workflow reads the clock directly; every cutoff test pins a fixed instant and asserts an exact
  date. The same window scanned twice in one day produces the same cutoff both times.
- **Adding a supplier and teaching the app to read its invoices is done entirely on screen** — no
  rebuild, no F# change, no restart. That is the whole point of ask #6, and it is the acceptance test
  for it.
- Suppliers, templates and invoices live in the main SQLite database, with the schema built **only**
  by FluentMigrator — no DDL in a store function, a test, or a SQLite tool.
- A template carrying a pathological regex fails *that rule* with a named error inside its timeout,
  and the scan finishes.
- **The four worked templates are in the test suite as fixtures**, each asserting every field it
  claims to extract. They are the only extraction tests in this build written against layouts that
  demonstrably exist.
- Normalization is applied identically at authoring time and at scan time, and there is a test
  proving a non-breaking space, a hard-wrapped label and a blank line between label and value all
  still match. Each was found in the real mailbox and each fails silently without it.
- `MyDogsbody.Domain` still names no Thunderbird, Google, LiteDB, SQLite, MIME or PDF type — the
  ledger got bigger, the centre did not get less pure.
- **A scan completes with Thunderbird open**, against both an mbox account and a maildir one, without
  Thunderbird noticing and without corrupting anything. Read-only by construction — opened for read
  with `FileShare.ReadWrite`, never copied, never written — and tested that way.
- **The accounts page lists exactly the 10 accounts `prefs.js` declares** for the measured profile —
  not the 15 directories under `ImapMail/`, and not the six orphans. **That number is the acceptance
  test for discovery.**
- **No `Credentials.db`, no `MyDogsbody.Integrations.Credentials`, no `MyDogsbody.Enums`, and no
  `MyDogsbody.Domain/Credentials/`.** Each provider's credentials sit in that provider's own
  database. Three projects deleted against one added, so the solution is **two projects smaller**.

---

## The three numbers to carry into implementation

1. **12% → 39%.** Only 12% of the invoice PDFs state a reference, an amount *and* a due date.
   `DateFromField` plus per-supplier payment terms takes that to 39%. **Change #7's value depends on
   this more than on anything in its own scope.**
2. **91% vs 8%.** The invoice fields are in the PDF attachment, essentially never in the email body.
   **Build the PDF path first and treat the body reader as the exception it is.**
3. **16 GB, 6.2 GB in scope.** Reading is in place with `FileShare.ReadWrite`, never copied, and
   incremental after the first pass. The header-only scan of 234,446 messages was fast; **opening
   bodies is what costs.**

---

## What the pre-proposal got wrong, and how it was caught

Kept because it is the argument for measuring before building.

- **The profile measurement** overturned structural account detection (would have found 15 accounts
  where 9 exist) and copy-then-parse (impossible at 16 GB).
- **The mailbox measurement** overturned the rule-kind ranking, the value of the email body, the
  "prefer text/plain" default, and the assumption that no normalization step was needed — that last
  one silently, since a non-breaking space is enough to make a correct-looking rule never match.
- **Three holes appeared between separately reasonable answers**, none visible from any single one:
  a transient problem list emptied by incremental scanning (Q1.19), hand-deleted invoices resurrected
  by upsert (Q5.14), and a pure diff that could delete calendar entries it did not own (friction #18).

Every one of those would have surfaced during implementation instead.

## The one thing still unproven

**No code has been written, and no template has been run by MyDogsbody itself.** The rule set is
derived from real documents and its four worked templates are drawn from layouts that demonstrably
exist — but they were exercised by a scratch script, not by `ApplyTemplateWorkflow`. **Making those
four the first fixtures in change #2's test suite is what closes the gap**, and it is the cheapest
acceptance test available: the layouts are known, the expected fields are known, and the templates
are already written down.
