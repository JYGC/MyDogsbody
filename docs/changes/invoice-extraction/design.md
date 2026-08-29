# Design — Invoice extraction

Change **#4 of 7**. Depends on **#1, #2, #3**. Requirements in [`requirements.md`](requirements.md);
decision record and measurements in
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md).

---

## What this change joins together

| From | Gives this change |
| --- | --- |
| #1 `invoice-ledger-foundation` | Suppliers, their matchers, their payment terms; the proven main-database path |
| #2 `invoice-templates` | The rule engine, `MatchSupplierWorkflow`, `SelectTemplateWorkflow`, `ExtractedInvoice`, `TextNormalization` |
| #3 `thunderbird-account-selection` | `ReadMailFolder`, `ScanCutoff`, the selected account, watermarks |

What it adds: the four document readers, validation into a `ValidInvoice`, the ledger, the problem
list, tombstones, the scan-window apparatus, and the page that shows all of it.

**Two numbers shape it.** *Finding 1*: the invoice fields are in the PDF attachment 91% of the time
and in the body 8% — so the PDF path is built first and the body reader is treated as the exception
it is. *Friction #14*: the mail store is 16 GB with 6.2 GB in scope, so the cutoff is applied while
reading and rescans are incremental.

---

## System architecture and components

```
 UI.Portal  /invoices                          /settings/scan-windows
   InvoicesPage.fs                               ScanWindowsPage.fs
   InvoicesComponents.fs (table, problems,       ScanWindowsComponents.fs
     tombstones, window picker)
   InvoicesModuleCreators.fs                     ScanWindowsBrowserModuleCreators.fs
        ▼
 UI.Types   InvoiceApi    { Scan; GetInvoices; DeleteInvoice; GetProblems;
                            GetTombstones; UndeleteInvoice }
            ScanWindowApi { GetScanWindows; AddScanWindow; DeleteScanWindow;
                            GetSelectedScanWindow; SelectScanWindow }
        ▼
 Startup    InvoiceApiFactory.fs · InvoiceApiMappers.fs
            ScanWindowApiFactory.fs · ScanWindowApiMappers.fs
            Startup.fs — binds GetCurrentTime, the document-format dispatcher, both APIs
        ▼
 Domain     Documents/DocumentsTypes.fs      + DocumentSource, ReadDocumentText
            Invoices/InvoicesTypes.fs        + constrained primitives, ValidInvoice,
                                               StoredInvoice, ScanWindow*, ScanProblem,
                                               InvoiceTombstone, the rest of InvoiceError
            Invoices/ScanMessageWorkflow.fs      mail message → ScannedMessage
            Invoices/ValidateInvoiceWorkflow.fs  ExtractedInvoice → ValidInvoice
            Invoices/ScanForInvoicesWorkflow.fs  the orchestration
            Invoices/DeleteInvoiceWorkflow.fs · UndeleteInvoiceWorkflow.fs
            Invoices/ResolveScanWindowWorkflow.fs
            Invoices/{Add,Delete,List,Select}ScanWindowWorkflow.fs
        ▲
 Integrations.Documents   ← MyDogsbody.Integrations.Pdf, RENAMED
   PdfDocumentReader.fs      (moves across, contract suite intact)
   WordDocumentReader.fs     .docx via DocumentFormat.OpenXml
   PlainTextDocumentReader.fs
   EmailBodyReader.fs        prefers the alternative that preserves block structure
        ▲
 Database   InvoiceStore.fs · ScanWindowStore.fs · their record mappers
        ▲
 Migrations 20260810000004 Invoices          …0005 ScanProblems
            …0006 InvoiceTombstones          …0007 ScanWindows (+ seed)
            …0008 InvoiceSettings
```

**Reserved migration timestamps for this change: `20260810000004`–`20260810000008`.**
*(Renumbered from the originally reserved `20260809000005`–`…0009` per
[background → *Migration timestamps, reserved across the series*](../invoice-to-calendar/background.md#migration-timestamps-reserved-across-the-series)
— change #1's `20260810000001` and change #2's `…0002`–`…0003` sort above the old block.)*

### `Integrations.Pdf` becomes `Integrations.Documents`

One project per **capability**, not per library (Q1.14). `PdfDocumentReader.fs` moves across with its
contract suite intact and three readers join it. That is what keeps the composition root binding
`ReadDocumentText` **once**, in one place, for all four formats — and why a fifth format later is a
file rather than a project.

The rename is a `git mv` of two directories plus namespace edits. The
`logging-not-an-integration` change recorded that Windows may refuse a directory rename while an IDE
language server holds handles on it; the workaround there was to delete `bin/`and `obj/` **first**,
then `git mv` file by file. Expect the same.

---

## Data models and interfaces

### `Documents/DocumentsTypes.fs` — the second reading capability

```fsharp
/// Bytes, not a path: an attachment lives inside a 2.5 GB mbox, and it should not have to be
/// spilled to a temp file (and cleaned up, and kept out of a backup) to be read. Q1.11.
type DocumentSource = { Format: DocumentFormat; Name: string; Content: byte[] }

/// One type for all four formats. The composition root binds one reader per format and
/// dispatches on Format, so the domain sees a single function.
///
/// This does NOT replace ReadDocumentContent, which takes a DocumentPath and returns
/// coordinate-bearing Words. Both live here, both are satisfied by PdfDocumentReader, and each
/// owes its own contract suite. A DocumentSource and a DocumentPath are different enough types
/// that the composition root cannot bind the wrong one silently.  Friction #7.
type ReadDocumentText = DocumentSource -> Result<TextLine list, DocumentError>
```

`DocumentError` gains `DocumentFormatUnsupported of format: string` and
`DocumentHasNoTextLayer` — the two causes the measurement says will actually occur (34 of 644 PDFs,
and every `.xlsx`).

### `Invoices/InvoicesTypes.fs` — extended, not replaced

Change #2 created this file with `SourceMessageId`, `MessagePart`, `ScannedMessage`,
`ExtractedInvoice` and part of `InvoiceError`. This change adds:

```fsharp
/// The supplier's own invoice number.
///
/// create FOLDS INTERNAL WHITESPACE. One measured utility prints its reference in three
/// space-separated groups and names the attachment with the same digits unspaced - under the
/// natural key those would be two keys for one invoice, two ledger rows and two calendar events.
type InvoiceReference = private InvoiceReference of string
module InvoiceReference =
    let create (value: string) : Result<InvoiceReference, string> = …   // reuses change #2's fold

type Money = private Money of decimal * string          // amount + currency code
type IssueDate = private IssueDate of System.DateTime
type DueDate   = private DueDate   of System.DateTime
type InvoiceId = private InvoiceId of string

/// A window is a ROW, not a case. The seeded five are a starting set, not the whole set, so the
/// guarantee a closed union would have given moves into this create - the same move a
/// user-authored template already forces.
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

type ScanWindowId = private ScanWindowId of string
type StoredScanWindow = { Id: ScanWindowId; Days: ScanWindowDays }

type ValidInvoice =
    { SupplierId: SupplierId
      TemplateId: TemplateId
      SourceMessageId: SourceMessageId
      Reference: InvoiceReference
      Amount: Money
      IssueDate: IssueDate option
      DueDate: DueDate option }              // option - Q1.10

type StoredInvoice = { Id: InvoiceId; Invoice: ValidInvoice; ScannedAt: System.DateTime }

type ScanProblemCause =
    | NoSupplierMatched
    | SeveralSuppliersMatched of SupplierId list
    | NoTemplateMatched       of SupplierId
    | RuleFoundNothing        of SupplierId * TemplateId * field: string
    | AttachmentUnreadable    of fileName: string * reason: string
    | FormatUnsupported       of fileName: string * format: string
    | ValueUnparseable        of field: string * raw: string
    | RuleTimedOutCause       of SupplierId * TemplateId * field: string

type ScanProblem =
    { SourceMessageId: SourceMessageId
      Sender: string
      Subject: string
      ReceivedAt: System.DateTime
      Cause: ScanProblemCause
      RecordedAt: System.DateTime }

/// The Q5.14 record that keeps a hand-deleted invoice deleted. Keyed on the NATURAL key, not the
/// database id, so rebuilding the ledger does not resurrect what you removed.
type InvoiceTombstone =
    { SupplierId: SupplierId; Reference: InvoiceReference; DeletedAt: System.DateTime }

type ScanResult = { Invoices: StoredInvoice list; Problems: ScanProblem list }

// --- dependency function types added here ---

/// CLAUDE.md forbids the domain reading a clock, and "N days back from today" needs one.
type GetCurrentTime = unit -> System.DateTime

type LoadInvoices   = ScanCutoff option -> Result<StoredInvoice list, InvoiceError>
type UpsertInvoices = ValidInvoice list -> Result<StoredInvoice list, InvoiceError>
type DeleteInvoice  = InvoiceId -> Result<StoredInvoice option, InvoiceError>

type LoadTombstones   = unit -> Result<InvoiceTombstone list, InvoiceError>
type SaveTombstone    = InvoiceTombstone -> Result<unit, InvoiceError>
type RemoveTombstone  = SupplierId -> InvoiceReference -> Result<bool, InvoiceError>

type LoadScanProblems  = unit -> Result<ScanProblem list, InvoiceError>
type SaveScanProblems  = ScanProblem list -> Result<unit, InvoiceError>
type ClearScanProblems = SourceMessageId list -> Result<unit, InvoiceError>

type LoadScanWindows  = unit -> Result<StoredScanWindow list, InvoiceError>
type SaveScanWindow   = ScanWindowDays -> Result<StoredScanWindow, InvoiceError>
type DeleteScanWindow = ScanWindowId -> Result<bool, InvoiceError>
type LoadSelectedScanWindow = unit -> Result<ScanWindowDays option, InvoiceError>
type SaveSelectedScanWindow = ScanWindowDays -> Result<unit, InvoiceError>
```

`InvoiceError` gains `InvoiceReferenceInvalid`, `AmountInvalid`, `SupplierGone`,
`ScanWindowInvalid of reason`, `ScanWindowAlreadyExists of days`, `CannotDeleteLastScanWindow`,
`InvoiceNotFound`, `NoAccountSelected` and `InvoiceStoreFailed`.

`CannotDeleteLastScanWindow` is **a rule, not a UI guard**: the picker must always have something to
offer, so emptying the list is refused in the domain rather than handled downstream.

### Workflows

| File | Signature | Notes |
| --- | --- | --- |
| `ScanMessageWorkflow.fs` | `ReadDocumentText -> MailMessage -> ScannedMessage * ScanProblemCause list` | Flattens a message and its attachments to text. Unreadable attachments become problem causes rather than failures — a message with one bad attachment and one good one still yields an invoice |
| `ValidateInvoiceWorkflow.fs` | `ExtractedInvoice -> Result<ValidInvoice, InvoiceError>` | **Pure.** Turns change #2's parsed values into constrained types |
| `ResolveScanWindowWorkflow.fs` | `StoredScanWindow list -> ScanWindowDays option -> ScanWindowDays` | **Pure, total.** Three cases, one place |
| `ScanForInvoicesWorkflow.fs` | see below | The orchestration |
| `DeleteInvoiceWorkflow.fs` | `DeleteInvoice -> SaveTombstone -> GetCurrentTime -> string -> Result<unit, InvoiceError>` | Delete **and** tombstone, in that order |
| `UndeleteInvoiceWorkflow.fs` | `RemoveTombstone -> SupplierId -> InvoiceReference -> Result<unit, InvoiceError>` | |
| `AddScanWindowWorkflow.fs` | `LoadScanWindows -> SaveScanWindow -> int -> Result<StoredScanWindow, InvoiceError>` | Rejects duplicates and out-of-bounds |
| `DeleteScanWindowWorkflow.fs` | `LoadScanWindows -> DeleteScanWindow -> string -> Result<unit, InvoiceError>` | Refuses the last one |
| `ListScanWindowsWorkflow.fs` | `LoadScanWindows -> unit -> Result<StoredScanWindow list, InvoiceError>` | Ordered ascending |
| `SelectScanWindowWorkflow.fs` | `LoadScanWindows -> SaveSelectedScanWindow -> int -> Result<ScanWindowDays, InvoiceError>` | Refuses a window not in the list |

```fsharp
let scanForInvoices
    (getCurrentTime: GetCurrentTime)
    (loadSelectedMailAccount: LoadSelectedMailAccount)
    (readMailFolder: ReadMailFolder)
    (readDocumentText: ReadDocumentText)
    (loadSuppliers: LoadSuppliers)
    (loadTemplatesForSupplier: LoadTemplatesForSupplier)
    (loadTombstones: LoadTombstones)
    (upsertInvoices: UpsertInvoices)
    (saveScanProblems: SaveScanProblems)
    (clearScanProblems: ClearScanProblems)
    (window: ScanWindowDays)
    : Result<ScanResult, InvoiceError>
```

Note how much of it is calls to the pure workflows from change #2 — that is the shape to aim for.
The cutoff arithmetic is a **private pure function in this file**, so "180 days back from 5 January"
is a unit test with a fixed clock and no mail store anywhere near it.

### Migrations

| Timestamp | Name | Creates |
| --- | --- | --- |
| `…0004` | `CreateInvoicesTable` | `Invoices(Id, SupplierId FK, TemplateId FK, Reference, Amount, Currency, IssueDate NULL, DueDate NULL, SourceMessageId, ScannedAt)` + **unique index on `(SupplierId, Reference)`** |
| `…0005` | `CreateScanProblemsTable` | `ScanProblems(Id, SourceMessageId, SupplierId NULL, Sender, Subject, ReceivedAt, Cause, Detail, RecordedAt)` + index on `SourceMessageId` |
| `…0006` | `CreateInvoiceTombstonesTable` | `InvoiceTombstones(Id, SupplierId FK, Reference, DeletedAt)` + unique index on `(SupplierId, Reference)` |
| `…0007` | `CreateScanWindowsTable` | `ScanWindows(Id, Days)` + unique index on `Days`, **and `Insert.IntoTable` for 7, 14, 30, 90, 180** |
| `…0008` | `CreateInvoiceSettingsTable` | `InvoiceSettings(Id INTEGER PK CHECK(Id = 1), SelectedScanWindowDays INTEGER NULL)` |

**`…0007` is a new precedent in this repository** (friction #17): every migration so far creates
schema and nothing else. The alternatives are worse — `Startup.fs` checking on each launch whether it
ought to write five rows is runtime schema management by another name, and hard-coding them in a
component is the thing this whole change is undoing. `Insert.IntoTable` in `Up`, matching
`Delete.FromTable` in `Down`. **Say so plainly in the change description, because the next person
will copy whichever migration they open first.**

Consequence accepted knowingly: **if you delete a seeded window, re-running migrations will not bring
it back.** That is correct for a value you chose to remove, and it is why `ScanWindowDays.fallback`
exists rather than the code assuming 14 is present.

---

## Sequence diagrams

### A scan

```
InvoicesPage          InvoiceApi           ScanForInvoicesWorkflow          adapters
  │ window changed        │                        │
  ├─ SelectScanWindow ───►│ persist                │
  ├─ Scan days ──────────►├───────────────────────►│
  │                       │                        ├─ getCurrentTime() .Date .AddDays(-days)
  │                       │                        │     → ScanCutoff        ← pure, fixed-clock tested
  │                       │                        ├─ loadSelectedMailAccount
  │                       │                        │     None → NoAccountSelected, STOP
  │                       │                        ├─ readMailFolder account cutoff ──────► mbox
  │                       │                        │     (cutoff applied on HEADERS; 6.2 GB in scope)
  │                       │                        ├─ loadSuppliers · loadTombstones
  │                       │                        │
  │                       │                        │  per message:
  │                       │                        │   ├ ScanMessageWorkflow → ScannedMessage
  │                       │                        │   │    attachments dispatched by EXTENSION
  │                       │                        │   │    unreadable one → a problem cause, NOT a stop
  │                       │                        │   ├ MatchSupplierWorkflow            (change #2)
  │                       │                        │   │    none → problem, next message
  │                       │                        │   │    several → problem naming ALL, next message
  │                       │                        │   ├ loadTemplatesForSupplier
  │                       │                        │   ├ SelectTemplateWorkflow           (change #2)
  │                       │                        │   │    first complete match wins
  │                       │                        │   ├ ValidateInvoiceWorkflow
  │                       │                        │   └ tombstoned key? → SKIP silently
  │                       │                        │
  │                       │                        ├─ upsertInvoices  (natural key → update, not add)
  │                       │                        ├─ saveScanProblems
  │                       │                        └─ clearScanProblems for messages that now succeeded
  │◄─ ScanResult { Invoices; Problems } ───────────┤
  └─ transact: table, count, window label, problems badge
```

### Resolving which window the page opens on

```
ResolveScanWindowWorkflow  (pure, total, three cases)

  stored windows        remembered        →  opens on
  ────────────────────  ────────────────     ─────────────────────────────────
  [7;14;30;90;180]      None              →  14        (fresh database)
  [7;14;30;90;180]      Some 90           →  90        (last choice, still present)
  [7;30;90;180]         Some 14           →  14 gone → fallback 14 also gone → 7
  [7;14;30;90;180]      Some 45           →  14        (remembered window since deleted)

  ► The third row is the case nobody tries by hand, and it has its own unit test.
  ► The remembered choice is a NUMBER, not a foreign key: it survives its row being deleted,
    still means exactly what it meant, and simply is not offered by the picker any more.
```

### Delete, tombstone, rescan

```
user deletes invoice 42 (Acme / INV-1042)
   DeleteInvoiceWorkflow
     ├─ deleteInvoice 42            → the row is gone
     └─ saveTombstone (Acme, INV-1042, now)

next scan of a covering window
   ScanForInvoicesWorkflow
     ├─ extracts Acme / INV-1042 again
     ├─ its key is tombstoned  →  SKIPPED
     └─ the ledger stays as the user left it

   ► Without the tombstone, "delete" would mean "hide until the next scan" - which is the failure
     mode that ruled out hand-editing in the first place.
   ► The tombstone list is VISIBLE and reversible, because the day you delete a row because the
     TEMPLATE was broken, fix the template, and then find the corrected invoice silently skipped
     is the day this feature lies to you.
```

---

## Error-handling approach

Unchanged in shape: `InvoiceError` in the domain, `MyDogsbodyException` in the outer ring, meeting in
the two factories. `DocumentError` maps to `InvoiceError` as the scan binds the reader, the same way
the invoice workflows already borrow `LoadSuppliers` from a sibling area.

**The important split in this change is not error type but error *destination*.** Most failures here
are not errors at all — they are **problems**, recorded against a message and shown on screen:

| Situation | Becomes |
| --- | --- |
| No supplier matched; several matched; no template; a rule found nothing; an attachment unreadable; a format unsupported; a value unparseable; a rule timed out | A **`ScanProblem` row**. The scan continues |
| No account selected; the scan window is invalid; deleting the last window | An `InvoiceError`, rendered in the alert, expected, **not logged** |
| The store or the mail reader failed outright | An `InvoiceError` wrapping the real exception, **logged once** |

That is Q1.5 made structural: *a message that yields no invoice is a result, not a silence*. And
Q1.19 is why it persists — without a row, incremental scanning empties the list the moment you
rescan and the diagnostic is gone before you look.

### Action names

```
ActionNames.MyDogsbody.Integrations.Documents.PdfDocumentReader.readContent
ActionNames.MyDogsbody.Database.InvoiceStore.*  /  .ScanWindowStore.*
ActionNames.MyDogsbody.Startup.InvoiceApi.*     /  .ScanWindowApi.*
```

`ActionNames.MyDogsbody.Integrations.Pdf.*` is **renamed** to `.Documents.*` (only the existing
`PdfDocumentReader.readContent` entry). The structural suite will catch any entry left behind.

**The four `readText` readers carry no `ActionName` and return `Result<TextLine list, DocumentError>`
directly** — the `MailFolderReader` precedent this section's own error table leans on. Every outcome
a reader can produce is a domain fact a person could name: a scanned-image PDF
(`DocumentHasNoTextLayer`), a file that will not open (`DocumentUnreadable`), a format with no
reader (`DocumentFormatUnsupported`). A `handleError`/`MyDogsbodyException` shape would flatten
those three into one wrapped exception the composition root could not tell apart, and none of them
is the "infrastructure collapsed" case that shape exists for. An unexpected library crash is caught
by a catch-all and mapped to `DocumentUnreadable ex.Message`. `readContent` keeps its existing
outer-ring shape untouched (task 1.3) — it predates this convention and feeds a workflow this change
does not wire.

*(This is a departure from the design's original Action-names block, which speculatively listed a
`readText` entry per reader. Recorded here per background.md's instruction to state disagreements
with the decision record in the change's own `design.md`.)*

---

## Testing strategy

### The clock's contract suite — friction #15, stated rather than skipped

`GetCurrentTime` is a dependency function type, and CLAUDE.md calls those published interfaces owing
a suite run against the real implementation *and* every fake. The real implementation is
`DateTime.Now`, whose whole nature is to return something different each call, so "assert both sides
agree" has no meaning.

**What the suite actually asserts**, for the real clock and every fake alike:

1. Two successive calls are non-decreasing.
2. The `Kind` is what the composition root promises.
3. For the real clock only, the value is within a tolerance of `DateTime.Now` at the point of test.

**And the part with actual logic — the cutoff arithmetic — is unit-tested against fixed instants in
the workflow**, which is where the behaviour worth testing lives. This is written down here so the
clock is not quietly the one dependency type with no contract test.

### Unit

- Every `create`: one accepted and one rejected value per rule, reason asserted. `ScanWindowDays`
  gets both bounds. `InvoiceReference` gets the **whitespace-folding** case.
- `ResolveScanWindowWorkflow`: all four rows of the table above, including the deleted-fallback case.
- Cutoff arithmetic with a fixed clock: an exact date asserted; **the same window at 09:00 and 17:00
  gives the same cutoff**; 180 days back across a year boundary; days are uniform, so there is no
  month-end trap to test for.
- `ValidateInvoiceWorkflow`: Ok with every field; each failure with its payload; **a missing due date
  is Ok, not an error**.
- `ScanForInvoicesWorkflow` with **every dependency a lambda** — no mail store, no database, no
  files: a message yielding an invoice; a message yielding a problem and the scan continuing; a
  tombstoned key skipped; a rescan updating rather than duplicating; problems cleared for messages
  that now succeed; `NoAccountSelected` short-circuiting **with `readMailFolder` never called**.
- Scan-window CRUD: duplicate refused; out-of-bounds refused; **the last window's deletion refused**;
  selecting a window not in the list refused; the store dependency never called on any refusal.

### Integration

- Each reader against committed fixture documents: a normal PDF; **a PDF with no text layer**; **a
  PDF that cannot be opened**; a `.docx`; a legacy `.doc` (→ unsupported, naming the format); an
  `.xlsx` (→ unsupported, naming the format); a message with both body alternatives; an HTML body
  whose table structure must survive as block boundaries.
- Format dispatch **by extension**, including a PDF declared `application/octet-stream` and one
  declared `application/.pdf`.
- The ledger against a real temp SQLite database: upsert on the natural key; the unique index
  refusing a duplicate; tombstone round trip; problem rows written and cleared.
- Both factories with real stores bound, without touching `Startup.fs`.
- The seeding migration: `Up` inserts exactly five rows; **`Down` removes them**; re-running
  migrations after a user deletes one does **not** restore it.

### Contract

- One shared suite per new dependency function type, real adapter and every fake. That is sixteen
  types; **`ReadDocumentText` and `ReadDocumentContent` each get their own**, because two types that
  both mean "read a document" is exactly the situation where one suite covering both would hide a
  wrong binding (friction #7).
- Both new mappers, field-for-field, both directions.
- `ScanProblemCause` exhaustively — every case round-trips through its persisted encoding.
- Every `InvoiceError` case → its intended `MyDogsbodyException`, with the expected/unexpected split
  asserted.
- Persisted-shape tests for all five new tables.
- The renamed `ActionNames` module: no entry left under `Integrations.Pdf`.

### E2E

A scan producing invoices; a window change persisting and rescanning; an invoice with no due date
shown greyed with its reason; a per-row delete producing a tombstone and the row disappearing; an
un-delete; a problem row appearing for a message that yields nothing, with sender and subject shown;
a scan failure showing an alert with exactly one entry logged. Assert logging through a recording
`HandleErrorBuilder`. No test reaches `Startup.Startup`.

### Manual measurement — required

**Q1.9's immediate rescan is provisional until this is measured.** Run a real scan against the real
mail store and record in `outcome.md`: the duration of the first (cold, full) scan; the duration of a
second scan with watermarks in place; and the duration of a window change from 14 to 180 days.

**If a window change costs seconds, the immediate rescan is replaced by an explicit Refresh button.**
That was the condition Q1.9 was accepted under, and this is where it is settled.

---

## Decisions taken

1. **`InvoiceApi` and `ScanWindowApi` are separate records**, and neither is called `InvoiceSyncApi`.
   The scan-window API is consumed by *two* surfaces — the settings page that maintains the list and
   the invoices page's picker — and neither should take the whole invoice API to read five numbers.
   Change #7 adds `InvoiceSyncApi` as a third.
2. **`UploadableInvoice` is not declared here.** It is the stage type that makes "no due date cannot
   become an event" a compile-time fact, and its only consumer is change #7's sync workflow. The
   invoices page derives "greyed out" from `DueDate` being `None`.
3. **A partly unreadable message still yields an invoice.** `ScanMessageWorkflow` returns a
   `ScannedMessage` **and** a list of problem causes rather than a `Result`, so one corrupt
   attachment out of two does not lose the invoice in the other.
4. **Problems are cleared per source message, not wholesale.** A scan clears only the rows for
   messages it processed and which now succeed. A narrower window must not silently erase the
   diagnostics for messages outside it.
5. **Tombstones are keyed on the natural key, not the invoice id.** Rebuilding the ledger from
   scratch must not resurrect what the user removed.
6. **The remembered scan window is a number, not a foreign key.** No cascade rule to write, no
   dangling reference; it survives its row being deleted and simply is not offered any more.
7. **`InvoiceSettings` is a typed single-row table** (Q5.13). A `Settings(Key, Value)` table would
   take one migration and never need another, at the cost of every setting being a string parsed at
   the point of use. Adding a column by migration is already the rule for every other schema change
   here, so this is the consistent answer rather than the clever one — and **this is the first
   setting, so whatever it does becomes the habit.**
8. **The scan reads the account the user selected in change #3, and refuses with a named error when
   none is selected.** Not a silent empty result.
9. **`EmailBodyReader` prefers HTML** — the correction *Finding 5* makes to Q1.13. A template
   never has to know which alternative it got. *Mechanism, decided in implementation:* change #3's
   `MailFolderReader` already did the MIME work and hands over `BodyText` and `BodyHtml`
   separately. `ScanMessageWorkflow` picks — HTML if present (`DocumentSource` with
   `Format = EmailBody`, routed to `EmailBodyReader`), otherwise the plain text (`Format = PlainText`,
   routed to `PlainTextDocumentReader`). This keeps the single `ReadDocumentText` dependency the
   workflow table shows: `EmailBodyReader` only ever parses HTML — deriving block boundaries from
   `<tr>`/`<td>`/`<p>` structure and stripping markup where there is none — and the trivial
   "which alternative exists" choice is a one-line `Option.orElse` in the workflow, not multipart
   parsing. `EmailBodyReader` takes `HtmlAgilityPack` (offline-cached 1.11.39); it is the only
   project that does.
10. **`UpsertInvoice` is per-invoice, not `UpsertInvoices` batch** (implementation). "Continue past
   a failure and report per row" needs each row's constraint failure to become one message's
   problem; a batch that fails atomically cannot say which row. A rescan of an overlapping window
   still updates rather than duplicates — each per-row upsert is upsert-on-natural-key.
11. **`ScanResult` carries what *this scan* did**, not a reload: `Invoices` = the rows upserted
   this run (which, since a scan of window N reads every message in window N, is every in-window
   invoice), `Problems` = the problems recorded this run. The page's full view comes from
   `InvoiceApi.GetInvoices` / `GetProblems` separately.
12. **"Two invoices from one message" is a store-layer property, verified in Phase 7.** The
   engine (`selectTemplate`) produces one `ExtractedInvoice` per message; the `Invoices` table's
   key is `(SupplierId, Reference)`, not `SourceMessageId`, so two rows *can* share a source
   message id — 7.2 asserts that against the real store.
13. **`SupplierGone` maps to the `NoSupplierMatched` problem cause.** It is unreachable in a
   single scan (`matchSupplier` only matches a loaded supplier and the upsert is in the same
   scan); the guard exists for a supplier deleted mid-scan by another process, and reuses an
   existing cause rather than adding a ninth requirements.md did not enumerate.
14. **`MailFolderReader` gets a streaming mbox reader — Phase 14, forced by the Phase 12
   measurement.** The reader change #3 shipped buffered each folder file whole and refused
   anything a Latin1 string cannot hold (~1 GiB); `read`'s `| Error _ -> []` then dropped that
   folder silently on every scan. The 12.4/12.5 measurement run (`MeasureScan`) hit it head-on:
   the maintainer's invoice mail lives in a 2.0 GB Gmail INBOX (`imap.googlemail-1.com/INBOX`,
   `outpost597100@gmail.com`), which contributed **zero** messages with nothing on screen — a
   direct violation of Q1.5 ("never silent, never fatal to the scan") that made the feature
   unable to see the invoices it exists to extract. The fix (`foldMboxSegments`) streams the file
   `StreamChunkBytes` (4 MiB) at a time, emitting one message segment at a time in memory bounded
   by that chunk plus one in-progress message; `bufferSpan` / `splitIntoMessages` / `processSegment`
   are gone, replaced by `segmentStartOffsets` (a byte-scan for `From ` boundaries), `foldMboxSegments`
   and `normalizeStartOffset`. `MaxBufferableBytes` survives as a **per-segment** ceiling only. A
   single segment larger than `MaxMessageBytes` (128 MiB — a corrupt file or a mis-split, never a
   real email) is emitted once for the caller to skip, then the reader byte-scans forward to the
   next real boundary rather than accumulating without limit. Watermarks, the torn-final-message
   rule, the false-`From `-boundary rule and the CRLF handling are all unchanged; the resume seam
   (a watermark landing one byte before a `\nFrom ` blank line) is trimmed in `foldMboxSegments`.
   `countMessages` no longer over-counts a non-last fragment with no header/body separator — it
   now counts a segment iff it has a separator, matching what `read` returns.
15. **A window change reloads; a scan is explicit — Phase 15, settled by the Phase 12 measurement.**
   Q1.9 ("changing the window rescans immediately") was accepted on the written condition that if
   a window change cost seconds, an explicit Refresh would replace it. The measurement put a full
   scan at **~60 s whatever the window** — 58 s at 180 days, 63 s at 730 — because the cost is
   reading every folder of every account, not the cutoff. So `InvoicesModuleCreators.selectWindow`
   now persists the choice and calls `loadLedger` (`GetInvoices` / `GetProblems` for the new
   window — no `InvoiceApi.Scan`); `deleteInvoice` likewise reloads (the row is hard-deleted, so
   `GetInvoices` already omits it). `InvoiceApi.Scan` is reached only by the initial load, the new
   **"Scan now"** button (`InvoicesComponents.windowPicker`, disabled while busy), and
   `undeleteInvoice` (only a scan can restore a hard-deleted row — `UndeleteInvoiceWorkflow` says
   so). `InvoicesModule` already carried a stubbed `Rescan` for exactly this. "Narrowing hides, it
   does not forget" now holds by construction: the store keeps every invoice, the window is a
   read filter.

---

## Risks

| Risk | Handling |
| --- | --- |
| **Friction #14 / Q1.9 — immediate rescan over 6.2 GB.** The whole responsiveness of the page rides on the cutoff being applied while reading and on change #3's watermarks | Measured, not assumed: the manual measurement above is a required task, and the fallback (an explicit Refresh) is written into the requirements rather than discovered later |
| **Friction #17 — a migration now carries data** | Stated in the change description, `Delete.FromTable` in `Down`, and a test that `Down` removes the seed |
| **Friction #15 — the clock has no natural contract test** | The suite and its rationale are written out above rather than skipped in silence |
| **Friction #7 — two "read a document" types** | Separate contract suites, and `DocumentSource` vs `DocumentPath` are different enough types that a wrong binding will not compile |
| **Friction #8 — a `.doc` skipped silently would look like "this supplier sends nothing"** | Unsupported format is a **problem row naming the format**, with a fixture per format |
| **This change is the largest after #2** | The scan-window apparatus is the designated split point — see `tasks.md`. Phases are ordered so it can be lifted out whole |
| **The `Integrations.Pdf` rename fails on Windows** because an IDE holds handles on the project directory | Known from the `logging-not-an-integration` change: delete `bin/` and `obj/` first, then `git mv` file by file, then fix the solution file by hand and check the diff is only the expected lines |
| **A rescan duplicates the ledger** | The natural key, the unique index as a backstop, and an explicit test that a rescan of an overlapping window leaves the count unchanged |
| **Deleting an invoice and having it come back** | Tombstones, visible and reversible, with a test for each direction |
