# Design — Invoice templates

Change **#2 of 7**. Requirements in [`requirements.md`](requirements.md); decision record and
measurements in [`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md).

---

## The problem this change has to solve twice

**Once for the user:** the number of suppliers is unknowable, so how a supplier's mail is read has to
be data typed on a page.

**Once for the architecture:** everywhere else in this codebase, invalid states are unrepresentable
because a type says so. A rule typed into a text box cannot get that. The guarantee has to move to a
**validation boundary** — `ValidTemplate`, produced by the Save button and constructible nowhere else
— and the tests have to carry the weight the compiler normally would (friction #9).

That is the whole shape of this change: a small pure engine, a validation function that is the only
door into it, and a lot of table-driven tests.

---

## System architecture and components

```
 UI.Portal  /settings/suppliers/{id}/templates
   TemplatesPage.fs ─ TemplatesComponents.fs (editor dialog + rule editors + test panel)
   TemplatesBrowserModuleCreators.fs
        ▼
 UI.Types   TemplateApi { GetTemplatesForSupplier; AddTemplate; EditTemplate;
                          DeleteTemplate; ReorderTemplates; TestTemplate }
        ▼
 Startup    TemplateApiFactory.fs · TemplateApiMappers.fs        (the TOP mapper)
        ▼
 Domain     InvoiceTemplates/InvoiceTemplatesTypes.fs    rule model + TemplateError
            InvoiceTemplates/TextNormalization.fs        the Finding-4 contract
            InvoiceTemplates/ValidateTemplateWorkflow.fs the ONLY door to ValidTemplate
            Invoices/InvoicesTypes.fs                    ScannedMessage, ExtractedInvoice,
                                                         InvoiceError  (created here,
                                                         extended in change #4)
            Invoices/MatchSupplierWorkflow.fs            pure
            Invoices/ApplyTemplateWorkflow.fs            pure — the engine
            Invoices/SelectTemplateWorkflow.fs           pure — first complete match wins
            InvoiceTemplates/{Add,Edit,Delete,List}TemplateWorkflow.fs
        ▲
 Database   TemplateStore.fs · TemplateRecordMappers.fs  (the BOTTOM mapper)
            DatabaseContext + GetInvoiceTemplates, GetTemplateFieldRules
        ▲
 Migrations 20260810000002_CreateInvoiceTemplatesTable
            20260810000003_CreateTemplateFieldRulesTable
```

**Reserved migration timestamps for this change: `20260810000002`–`20260810000003`.**

Renumbered from `20260809000003`–`…0004` after change #1 shipped a sixth migration,
`20260810000001` (the case-insensitive `IX_Suppliers_Name`, PR #8), which sorts above the whole
`20260809` block. Reasoning in
[background → *Migration timestamps*](../invoice-to-calendar/background.md#migration-timestamps-reserved-across-the-series).

### Where each new file lives, and why

`ScannedMessage`, `ExtractedInvoice`, `MatchSupplierWorkflow`, `ApplyTemplateWorkflow` and
`SelectTemplateWorkflow` go in **`Domain/Invoices/`**, not `InvoiceTemplates/`. A template answers
"given it is Acme, where are the fields?"; a message and what was pulled out of it are invoice
concerns. `InvoiceTemplates/` holds the rule model, its validation and its CRUD.

Compile order in `MyDogsbody.Domain.fsproj`:

```
Result.fs
Credentials/…                        (removed in change #5)
Documents/DocumentsTypes.fs          ← gains DocumentFormat and TextLine here
Documents/ReadDocumentLinesWorkflow.fs
Suppliers/…                          (change #1)
InvoiceTemplates/InvoiceTemplatesTypes.fs
InvoiceTemplates/TextNormalization.fs
InvoiceTemplates/ValidateTemplateWorkflow.fs
InvoiceTemplates/AddTemplateWorkflow.fs … ListTemplatesWorkflow.fs
Invoices/InvoicesTypes.fs
Invoices/MatchSupplierWorkflow.fs
Invoices/ApplyTemplateWorkflow.fs
Invoices/SelectTemplateWorkflow.fs
```

---

## Data models and interfaces

### `Documents/DocumentsTypes.fs` — two additions

```fsharp
type DocumentFormat = Pdf | Word | PlainText | EmailBody

/// A line of extracted text and the block it came from.
///
/// BlockIndex is what makes Finding 4's join rule expressible: a wrapped continuation may be
/// joined to its predecessor WITHIN a block, and never across one, because LinesAfterLabel
/// depends on the structure a block boundary marks. A reader assigns it - a paragraph, a table
/// cell, a PDF text block. Plain text splits on blank lines.
type TextLine = { Text: string; BlockIndex: int }
```

`DocumentSource` and `ReadDocumentText` arrive in change #4, with the adapters that satisfy them. A
dependency function type with no adapter and no consumer would owe a contract suite this change
could not write.

### `InvoiceTemplates/InvoiceTemplatesTypes.fs`

```fsharp
namespace MyDogsbody.Domain.InvoiceTemplates

open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers

type TemplateId = private TemplateId of string
module TemplateId = …

type TemplateName = private TemplateName of string      // non-empty, <= 100
module TemplateName = …

/// Which part of a message a template reads.
type DocumentPart =
    | Body
    | Attachment of DocumentFormat
    | AnyPart

/// The seven kinds, four measured as load-bearing and three added on evidence.
/// Deliberately small - every case is one the page must render an editor for and one the user
/// has to understand.
type FieldRule =
    | AfterLabel      of label: string
    | LinesAfterLabel of label: string * offset: int
    | RegexCapture    of pattern: string
    | FixedValue      of string
    | SubjectCapture  of pattern: string
    | AttachmentName  of pattern: string
    | DateFromField   of source: TargetField

and TargetField = Reference | Amount | Currency | IssueDate | DueDate

type ParseHint =
    | AsText
    | AsMoney of decimalSeparator: char
    | AsDate  of format: string          // explicit. NEVER DateTime.Parse with ambient culture

type TemplateFieldRule = { Field: TargetField; Rule: FieldRule; Hint: ParseHint }

type UnvalidatedTemplate =
    { SupplierId: string
      Name: string
      Part: DocumentPart
      Position: int
      Rules: TemplateFieldRule list }

/// The type that matters. Produced ONLY by ValidateTemplateWorkflow, accepted by the engine and
/// by nothing else. This is where the compile-time guarantee the rest of the domain enjoys is
/// replaced by a runtime boundary - friction #9, and the reason this change is test-heavy.
type ValidTemplate =
    private
        { SupplierId': SupplierId
          Name': TemplateName
          Part': DocumentPart
          Position': int
          Rules': TemplateFieldRule list
          CompiledPatterns': Map<TargetField, System.Text.RegularExpressions.Regex> }

module ValidTemplate =
    // read-only accessors; no constructor is exposed
    let supplierId (t: ValidTemplate) = t.SupplierId'
    …

type StoredTemplate = { Id: TemplateId; Template: ValidTemplate }

/// Can this template be SAVED? Apply-time failures are InvoiceError - see Decisions taken.
type TemplateError =
    | TemplateNameInvalid    of reason: string
    | PatternInvalid         of field: TargetField * reason: string
    | PatternHasNoCaptureGroup of field: TargetField
    | DateFormatInvalid      of field: TargetField * reason: string
    | OffsetOutOfRange       of field: TargetField * offset: int
    | RequiredFieldHasNoRule  of TargetField
    | DuplicateRuleForField   of TargetField
    | DerivationSourceMissing of source: TargetField
    | DerivationSourceNotADate of source: TargetField
    | TemplateNotFound       of TemplateId
    | TemplateSupplierNotFound of SupplierId
    | TemplateStoreFailed    of message: string

type LoadTemplatesForSupplier = SupplierId -> Result<StoredTemplate list, TemplateError>
type SaveTemplate    = ValidTemplate -> Result<StoredTemplate, TemplateError>
type UpdateTemplate  = TemplateId -> ValidTemplate -> Result<StoredTemplate option, TemplateError>
type DeleteTemplate  = TemplateId -> Result<bool, TemplateError>
type ReorderTemplates = SupplierId -> TemplateId list -> Result<unit, TemplateError>

/// The suppliers a template may be written for. Declared HERE rather than reusing the suppliers
/// area's LoadSuppliers, which returns Result<_, SupplierError>: a workflow in this area returns
/// TemplateError, so reusing it would mean a Result.mapError at every call site and one dependency
/// type spanning two error DUs - and therefore owing a contract suite in both areas.
///
/// The adapter is the same SupplierStore.getAll; only the error mapping in TemplateApiFactory
/// differs. That mapping is the factory's job, which is exactly where the two rings already meet.
type LoadSuppliersForTemplates = unit -> Result<StoredSupplier list, TemplateError>
```

`ValidTemplate` carries its **compiled** `Regex` objects. Compiling at validation time is what makes
`PatternInvalid` a save-time error rather than a scan-time surprise, and it means the engine never
constructs a `Regex` from user text at all.

### `InvoiceTemplates/TextNormalization.fs`

```fsharp
module MyDogsbody.Domain.InvoiceTemplates.TextNormalization

/// Finding 4's contract, in one place, applied identically at authoring time and at scan time.
/// It is public so the test panel can display exactly what the rules will see - Q7.6.6.
///
/// Order matters and is asserted: NFKC first (it is what turns some ligatures and fixed-width
/// forms into their plain equivalents), then space folding, then collapse, then trim, then the
/// within-block join, then drop empties. Reordering any two of these changes what matches.
let normalize (lines: TextLine list) : TextLine list = …

/// Whether a line looks like a wrapped continuation of its predecessor. Private, but its
/// behaviour is asserted through normalize: a line that starts lower-case and whose predecessor
/// does not end in a sentence terminator, within the same block.
let private isContinuation (previous: string) (current: string) : bool = …
```

### `Invoices/InvoicesTypes.fs` — created here, extended in change #4

```fsharp
namespace MyDogsbody.Domain.Invoices

type SourceMessageId = private SourceMessageId of string
module SourceMessageId = …

/// Which part of a message some text came from.
type MessagePart =
    | SubjectPart
    | BodyPart
    | AttachmentPart of name: string * format: DocumentFormat

/// A message and its attachments flattened to text, ready for a template.
///
/// Note it does NOT carry a supplier id: MatchSupplierWorkflow is what produces one FROM this,
/// so carrying one would be circular. The supplier lands on ExtractedInvoice instead.
type ScannedMessage =
    { SourceMessageId: SourceMessageId
      Sender: string
      Subject: string
      ReceivedAt: System.DateTime
      Parts: (MessagePart * TextLine list) list }

/// What a template pulled out and parsed with its own hints.
///
/// Deliberately NOT named UnvalidatedInvoice - see Decisions taken. It is still untrusted in the
/// domain sense: Reference is a plain string, Amount is an unconstrained decimal, Currency has
/// been compared to nothing. Change #4's ValidInvoice is where those become constrained types.
type ExtractedInvoice =
    { SupplierId: SupplierId
      TemplateId: TemplateId
      SourceMessageId: SourceMessageId
      Reference: string
      Amount: decimal
      Currency: string
      IssueDate: System.DateTime option
      DueDate: System.DateTime option }

/// What can go wrong turning a message into an invoice. Change #4 adds the storage and
/// mail-store cases to this same union.
type InvoiceError =
    | SupplierNotRecognised   of sender: string
    | MultipleSuppliersMatched of sender: string * suppliers: SupplierId list
    | NoTemplateForSupplier   of SupplierId
    | TemplateMatchedNothing  of template: TemplateId * field: TargetField
    | AmountUnparseable       of field: TargetField * raw: string
    | DateUnparseable         of field: TargetField * raw: string * format: string
    | RuleTimedOut            of template: TemplateId * field: TargetField
```

### Workflows

| File | Signature | Purity |
| --- | --- | --- |
| `ValidateTemplateWorkflow.fs` | `UnvalidatedTemplate -> Result<ValidTemplate, TemplateError>` | pure |
| `MatchSupplierWorkflow.fs` | `StoredSupplier list -> ScannedMessage -> Result<SupplierId, InvoiceError>` | pure |
| `ApplyTemplateWorkflow.fs` | `PaymentTermDays -> ValidTemplate -> ScannedMessage -> Result<ExtractedInvoice, InvoiceError>` | **pure** |
| `SelectTemplateWorkflow.fs` | `PaymentTermDays -> StoredTemplate list -> ScannedMessage -> Result<ExtractedInvoice, InvoiceError>` | pure |
| `AddTemplateWorkflow.fs` | `LoadSuppliersForTemplates -> LoadTemplatesForSupplier -> SaveTemplate -> UnvalidatedTemplate -> Result<StoredTemplate, TemplateError>` | dependencies first |
| `EditTemplateWorkflow.fs` | `LoadTemplatesForSupplier -> UpdateTemplate -> string -> UnvalidatedTemplate -> Result<StoredTemplate, TemplateError>` | |
| `DeleteTemplateWorkflow.fs` | `DeleteTemplate -> string -> Result<unit, TemplateError>` | |
| `ListTemplatesWorkflow.fs` | `LoadTemplatesForSupplier -> string -> Result<StoredTemplate list, TemplateError>` | ordered by position |

`ApplyTemplateWorkflow` has **no dependency parameters at all**. It touches no file, no clock and no
network, which is what makes table-driven tests over `(template, message) → expected fields` the
cheapest coverage in the whole feature — and where template bugs will actually be found.

### Migrations

| Timestamp | Name | Creates |
| --- | --- | --- |
| `20260810000002` | `CreateInvoiceTemplatesTable` | `InvoiceTemplates(Id INTEGER PK identity, SupplierId INTEGER NOT NULL FK → Suppliers.Id ON DELETE CASCADE, Name TEXT(100) NOT NULL, DocumentPart TEXT(32) NOT NULL, AttachmentFormat TEXT(16) NULL, Position INTEGER NOT NULL)` + index on `(SupplierId, Position)` |
| `20260810000003` | `CreateTemplateFieldRulesTable` | `TemplateFieldRules(Id INTEGER PK identity, TemplateId INTEGER NOT NULL FK → InvoiceTemplates.Id ON DELETE CASCADE, TargetField TEXT(16) NOT NULL, RuleKind TEXT(32) NOT NULL, RuleText TEXT(1000) NULL, RuleOffset INTEGER NULL, RuleSourceField TEXT(16) NULL, HintKind TEXT(16) NOT NULL, HintText TEXT(64) NULL)` + unique index on `(TemplateId, TargetField)` |

**Both tables carry a foreign key, so both are written with `Execute.Sql`, not the fluent
`Create.Table()` builder.** FluentMigrator's SQLite generator refuses `Create.ForeignKey` outright,
and SQLite has no `ALTER TABLE ADD CONSTRAINT` — the constraint has to be inline in `CREATE TABLE`,
which the fluent builder cannot express. `Migration_20260809000002_CreateSupplierMatchersTable.fs` is
the shape to copy: raw SQL for the table, the fluent builder again for the indexes and `Down()`.

`DocumentPart` splits across two columns because `Attachment of DocumentFormat` carries a payload —
one column for the case, one for its argument, nullable when the case has none. Same pattern for
`FieldRule` across `RuleText` / `RuleOffset` / `RuleSourceField`, and for `ParseHint` across
`HintKind` / `HintText`. **Columns, not a serialised blob** (Q5.10): a template is relational rows,
so an export is a read and a write, and a rule kind added later gets a column.

The unique index on `(TemplateId, TargetField)` is the database backstop for
`DuplicateRuleForField` — the same belt-and-braces the supplier-name index gives.

---

## Sequence diagrams

### Saving a template — the validation boundary

```
TemplatesPage      TemplateApi        AddTemplateWorkflow      ValidateTemplateWorkflow   TemplateStore
    │ save             │                     │                          │                     │
    ├─────────────────►│ AddTemplate uiType  │                          │                     │
    │                  ├─ toUnvalidatedTemplate                         │                     │
    │                  ├────────────────────►│ loadSuppliers ──────────────────────────────►  │
    │                  │                     │◄─ supplier exists                              │
    │                  │                     ├─────────────────────────►│ name
    │                  │                     │                          ├ every required field has a rule
    │                  │                     │                          ├ no duplicate field
    │                  │                     │                          ├ each pattern COMPILES
    │                  │                     │                          │   NonBacktracking, else
    │                  │                     │                          │   backtracking + timeout
    │                  │                     │                          ├ each pattern has a capture group
    │                  │                     │                          ├ each date format is real
    │                  │                     │                          ├ DateFromField source exists & is a date
    │                  │                     │◄─ Ok ValidTemplate ──────┤
    │                  │                     ├─ saveTemplate ─────────────────────────────►   │ INSERT
    │◄─ Ok () ─────────┤◄────────────────────┤
    ├─ reload (write-then-reload)
```

**A `ValidTemplate` exists only on the right-hand side of that `Ok`.** Nothing else constructs one,
so the engine below cannot be handed an unvalidated rule.

### Applying templates — first complete match wins

```
SelectTemplateWorkflow (pure)
   │ templates for supplier, in stored Position order
   ├─ filter: DocumentPart matches a part this message actually carries
   │      (AnyPart matches everything; Attachment Pdf needs a Pdf part present)
   │
   ├─ template 1 ──► ApplyTemplateWorkflow
   │                    ├─ TextNormalization.normalize on the selected parts
   │                    ├─ Reference rule  → "INV-1042"
   │                    ├─ Amount rule     → 412.50   (AsMoney '.')
   │                    ├─ Currency rule   → "AUD"    (FixedValue)
   │                    ├─ IssueDate rule  → 2026-07-14  (AsDate "d MMM yyyy")
   │                    ├─ DueDate rule    → DateFromField IssueDate
   │                    │                    + PaymentTermDays 30 → 2026-08-13
   │                    └─ Ok ExtractedInvoice
   └─ FIRST Ok wins. Its TemplateId is recorded on the result (Q7.6.3).

   If template 1 returns TemplateMatchedNothing, try template 2 …
   If ALL fail, return the error from the LAST template tried - a real diagnostic,
   not "nothing worked".
```

The measured case this exists for: one water utility labels the same field `Due date` for most
customers and `Direct debit` for direct-debit ones. Two templates, one supplier, tried in order.

### `DateFromField` — the 12% → 39% rule

```
Document states:  "Date: 14 Jul 2026"       ← issue date, 48% of documents
Document states:  (no due date)             ← 81% of documents

Supplier record:  PaymentTermDays = 30      ← change #1 put it here, on the supplier

ApplyTemplateWorkflow
   ├─ IssueDate: AfterLabel "Date:" + AsDate "d MMM yyyy"  → 2026-07-14
   └─ DueDate:   DateFromField IssueDate                   → 2026-07-14 + 30d = 2026-08-13

Without this rule: reference + amount + due date present in 12% of documents.
With it:                                                    39%.
```

The term lives on the **supplier**, not on the rule — otherwise one supplier's two templates could
disagree about when its own invoices fall due.

---

## Error-handling approach

Two domain error unions in this change, split by **when** the failure happens:

| Union | Answers | Raised by |
| --- | --- | --- |
| `TemplateError` | *Can this template be saved?* | `ValidateTemplateWorkflow`, the CRUD workflows |
| `InvoiceError` | *Did this message yield an invoice?* | `MatchSupplierWorkflow`, `ApplyTemplateWorkflow`, `SelectTemplateWorkflow` |

Outer ring unchanged: `TemplateStore` returns `Result<_, MyDogsbodyException>` written with
`handleError`, one `ActionNames.MyDogsbody.Database.TemplateStore.*` entry per function. The two meet
only in `TemplateApiFactory`.

Every `TemplateError` case except `TemplateStoreFailed` is an **expected** failure — the user typed
something that cannot work — so `TemplateApiMappers.toMyDogsbodyException` wraps an
`ApplicationException` and `handleError` passes it through **unlogged**. A `MudAlert` full of the
user's own typos is not a diagnostic log.

`InvoiceError` has no `ActionName` and never reaches `handleError` in this change: it is returned to
the test panel and rendered per field. Change #4 is where it becomes a persisted `ScanProblem` row.

### Regex safety in detail

```fsharp
let private compile (pattern: string) : Result<Regex, string * bool> =
    let timeout = TimeSpan.FromMilliseconds 250.0
    try
        Ok (Regex(pattern, RegexOptions.NonBacktracking ||| RegexOptions.IgnoreCase, timeout)), false
    with :? ArgumentException ->
        // NonBacktracking rejects lookaround, backreferences and RightToLeft. Fall back, but
        // KEEP the timeout - the timeout is the availability guarantee, NonBacktracking is only
        // the cheap way to make it unnecessary.
        try Ok (Regex(pattern, RegexOptions.IgnoreCase, timeout)), true
        with :? ArgumentException as ex -> Error ex.Message
```

The `bool` is "fell back to the backtracking engine", which the page reports at save time so the
user knows their pattern is on the slower path. At match time a `RegexMatchTimeoutException` is
caught and becomes `RuleTimedOut template field` — **that rule fails, the scan finishes**.

Measured context: `RegexCapture` fired exactly **once** in 1,199 candidates. It is a genuine escape
hatch, which is why its editor is the **last** one built here, not the first.

---

## Carried over from change #1's review

Change #1 was reviewed as five stacked PRs after it was written, and four of the defects it turned up
are **structural** — they land again in this change unless designed out, because this change writes
the same kinds of code against the same libraries. Three of the four were sitting under green tests.

### 1. Every multi-row write is transactional

`TemplateStore` has **three** multi-statement writes, more than `SupplierStore` had:

| Function | Statements |
| --- | --- |
| `insertOne` | one `InvoiceTemplates` row, then one `TemplateFieldRules` row per rule |
| `updateOne` | delete the template's rules, reinsert the new set, update the template row |
| `reorder` | one `UPDATE … SET Position` per template |

Without a transaction, a failure partway through leaves a **committed partial template** — a rule set
missing its last rule, or a reorder half applied — while the caller is told the write failed. That is
the exact defect `SupplierStore` shipped with. Copy `SupplierStore.inTransaction`; every one of the
three runs inside it.

**And it is asserted, not just written.** Change #1's fix has no test behind it: `SupplierStoreTests`
covers round trips and unreachable-store errors, but nothing there fails if `inTransaction` is
deleted. This change writes the test that change #1 didn't — force a mid-write failure (a rule row
that violates the `(TemplateId, TargetField)` unique index is the cheapest trigger) and assert the
template row is **absent** afterwards, not orphaned.

### 2. The top mapper returns `Result`; it never raises

`TemplateApiMappers` converts **four** unions from plain UI strings — `DocumentPart`, the `FieldRule`
kind, `TargetField` and `ParseHint`. Change #1's equivalent, `toMatcherKind`, used `failwith`, copied
from `CredentialApiMappers.toInfrastructure` where it is sound because that mapper takes a C# *enum*
the UI cannot forge. A string is not an enum. It raised inside the API call, outside any `handleError`
block, and the UI calls the API from `Async.Start` — so the exception became neither a `MudAlert` nor
a log entry and **the write silently never happened**.

Every string → union conversion in this change, in **both** mappers, returns
`Result<_, TemplateError>`. An unrecognised string is a named refusal, never a default and never an
exception. This is four times the surface change #1 had, so it is stated here rather than left to the
per-task instruction.

### 3. Dialogs are rendered through `MudDialogProvider` in E2E

Change #1's flow tests passed `(fun _ -> ())` for all three dialog callbacks and **never rendered the
dialog at all** — a `MudDialog` rendered standalone emits no markup, so an empty assertion looked like
a passing one. Two user-visible bugs lived behind that gap for the whole change, including a list
where every row rendered as an unlabelled button.

This change's dialog is far bigger: seven rule editors and a test panel. Its E2E tests render it the
way production shows it — `MudDialogProvider` plus `IDialogService.ShowAsync` — following
`E2E/SuppliersFlowTests.renderEditor`. Two specific traps that cost change #1:

- **`MudListItem` renders `Text` *or* child content, never both.** The rule list here is the same
  shape as the match-rule list that shipped unlabelled.
- **Remove list entries by index, not by value.** F# records compare structurally, so filtering by
  value deletes every identical entry. Two rules may legitimately be identical.

### 4. A contract suite constrains a fake only on the axes it asserts

Change #1's `SupplierApi` fake had drifted from the real API in three ways — no payment-term
validation, no matcher validation, and a "name already taken" message quoting the *submitted* spelling
where the real workflow quotes the *stored* one. The shared suite was green because it asserted none
of those behaviours. The rule the shared-suite requirement actually implies: **the suite pins
behaviour, not shape.** For `TemplateApi` that means each refusal in `requirements.md` is asserted
against the real record *and* every fake, with its message — not merely that a `Result` came back.

---

## Testing strategy

This change is where the tests replace a compiler, so the suite is larger than the code.

### Unit — the bulk of it

- **`TextNormalization`**, one test per clause of the contract plus one per measured failure mode:
  a non-breaking space between label and value; a label hard-wrapped across two lines; a blank line
  between label and value with `LinesAfterLabel(label, 1)`. **Each of those was found in the real
  mailbox and each fails silently without normalization.** Also: a join is *not* made across a block
  boundary, and the clause order is asserted by a case that changes meaning if two clauses swap.
- **`ApplyTemplateWorkflow`**, table-driven over `(template, message) → expected fields`, with a case
  per rule kind × parse hint that the model allows, every output field asserted.
- **The date-ambiguity pair**: `02/08/2016` with `d/M/yyyy` is 2 August; the same text with
  `M/d/yyyy` is 8 February. Two tests, because a six-month error that lands an event on the wrong day
  is the worst silent failure in the rule set.
- **`InvoiceReference` whitespace folding**: the same reference printed `1234 5678 90` and named
  `1234567890.pdf` yields one value. Under the natural key, failing this makes one invoice into two
  ledger rows and two calendar events.
- **`ValidateTemplateWorkflow`**, one test per refusal in the requirements, each asserting the exact
  case **and its payload** (which field, which reason).
- **`MatchSupplierWorkflow`**: one match; no match → `SupplierNotRecognised` with the sender; two
  matches → `MultipleSuppliersMatched` with **both** suppliers listed; a supplier with no rules never
  matches; case-insensitive comparison.
- **`SelectTemplateWorkflow`**: first complete match wins; a message matching only the second
  template still yields an invoice; when all fail the error comes from the **last** template tried;
  the winning `TemplateId` is on the result.
- **Regex safety**: a catastrophic-backtracking pattern (`(a+)+$` against a long non-matching
  string) fails with `RuleTimedOut` inside the timeout rather than hanging; a lookaround pattern
  saves successfully and is reported as being on the fallback path.
- **CRUD workflows**: Ok paths field-by-field, each error case with its payload, and
  dependency-not-called on every validation failure.

### The four measured fixtures

`Fixtures/MeasuredTemplates.fs` carries the four suppliers whose layouts proved consistent across
every sample, as **synthetic documents in the measured shapes** — layout only, values invented,
supplier names generic. Each asserts every field it claims to extract:

| Fixture | Proves |
| --- | --- |
| Invoice-management platform | `AfterLabel` reference, `LinesAfterLabel("Total", 1)` amount, and **`DateFromField IssueDate`** producing a due date the document never states |
| Water utility, template 1 | `LinesAfterLabel` for all three fields with a `Due date` label |
| Water utility, template 2 | the same document with `Direct debit` instead — **and that `SelectTemplateWorkflow` reaches it** |
| Accounting platform | a different rule kind per field in one document — `LinesAfterLabel` reference, `AfterLabel` due date |
| Energy retailer | `LinesAfterLabel` throughout with both `Due Date` and `Date of Issue` |
| Attachment-name variant | `AttachmentName "^(\d+)\.pdf$"` — the most reliable single field source measured |
| Subject variant | `SubjectCapture` — 17% of candidates carry the reference in the subject |

**No fixture contains a real amount, reference, account number, address or supplier name.** The
measurement deliberately kept those out of version control and this change keeps that true.

### Integration

- `TemplateStore` against a real SQLite temp file with the schema from `MigrationSetup.setupMigrations`.
  Call `SqliteConnection.ClearAllPools()` before deleting the temp file — Microsoft.Data.Sqlite pools
  connections and Windows keeps a pooled handle's file locked.
- Round trips for **every** rule kind, target field and parse hint — the split-column encoding is
  where a silent data loss would live.
- **Atomicity, per write.** A failure partway through `insertOne`, `updateOne` and `reorder` leaves
  **no** trace of the attempt — no orphaned template row, no half-replaced rule set, no partially
  applied order. Each is a test that fails if the transaction is removed. See *Carried over from
  change #1's review*, where this is the defect that shipped once already.
- Reorder persists and `getAll` returns the new order.
- Deleting a template removes its rules; deleting a **supplier** removes its templates and their
  rules (two cascades, both asserted — the `PRAGMA foreign_keys` guard from change #1 matters here
  twice).
- `TemplateApiFactory` with the real store bound, without touching `Startup.fs`.

### Contract

- One shared suite per dependency function type: `LoadTemplatesForSupplier`, `SaveTemplate`,
  `UpdateTemplate`, `DeleteTemplate`, `ReorderTemplates` — real adapter **and** every fake.
  `MemberData` source is a **public** `let`.
- Both mappers, field-for-field, both directions, **exhaustively over every union case**.
- **Exhaustiveness guards**: a test that enumerates `FieldRule`, `TargetField`, `ParseHint` and
  `DocumentPart` by reflection and fails if any case lacks a mapper entry, an editor and a fixture.
  Adding a rule kind should break this test until it is finished — that is its whole job.
- Each `TemplateError` case → its intended `MyDogsbodyException` action and message, with the
  expected/unexpected split asserted.
- Each new `ActionNames` entry reported by the function that declares it; the structural suite still
  passes.

### E2E

Add, edit, reorder, delete a template; a validation failure showing the specific reason with
**nothing logged**; a store failure with **exactly one entry logged**; and a test-panel run that
displays **normalized** text and per-field results. Assert logging through a recording
`HandleErrorBuilder`. No test reaches `Startup.Startup`.

**The dialog is rendered, not stubbed.** Every flow above that goes through the editor drives it via
`MudDialogProvider` + `IDialogService.ShowAsync`, per `E2E/SuppliersFlowTests.renderEditor` — passing
`(fun _ -> ())` for the dialog callbacks is what let two rendering bugs through change #1. At minimum:
each rule editor renders its own label, and deleting one of **two identical rules** removes exactly
one.

### Gate

Zero build errors; zero test failures; **zero skips**.

---

## Decisions taken

1. **`ApplyTemplateWorkflow` extracts *and* parses, returning `ExtractedInvoice` rather than
   `UnvalidatedInvoice`.** The pre-proposal's stage table had a template produce "plain strings" and
   validation parse them later. That does not survive `ParseHint` being part of the *template*: a
   parse failure is a template diagnostic the test panel must show against the rule that caused it,
   and `DateFromField` cannot be derived from an unparsed string at all. So the parse happens with
   the hint that selected it, and the result is named for what it is. `ExtractedInvoice` is still
   untrusted — change #4's `ValidInvoice` is where constrained types appear. **This renames a stage
   type in the original design; it does not add a hop.**
2. **`TemplateError` is save-time, `InvoiceError` is apply-time.** The pre-proposal put `RuleTimedOut`
   in `TemplateError`; a timeout happens when a rule *runs*, so it belongs with the other apply-time
   failures. `PatternInvalid` stays save-time, where it is a refusal the user can act on.
3. **`ScannedMessage` does not carry a `SupplierId`.** `MatchSupplierWorkflow` produces one *from* a
   scanned message, so carrying one would be circular. The supplier lands on `ExtractedInvoice`.
4. **`ValidTemplate` carries compiled `Regex` objects.** Compiling at the validation boundary is what
   makes `PatternInvalid` a save-time refusal, and it means the engine never builds a `Regex` from
   user text.
5. **`NonBacktracking` first, backtracking with the timeout as fallback.** `NonBacktracking` rejects
   lookaround and backreferences at construction, so insisting on it would refuse patterns that are
   perfectly safe. The **timeout** is the availability guarantee; `NonBacktracking` is the cheap way
   to make it unnecessary. The page says which path a pattern took.
6. **`Currency` is a required field.** Nothing in the measured sample needs an override — 96% of
   documents carry `$` and every one sampled is AUD — so `FixedValue "AUD"` per template is the
   normal answer, and requiring it means no invoice is ever stored with an unknown currency.
7. **The test panel takes pasted text, not a real message.** There is no mail reader until change #3.
   Change #4 adds a "test against a scanned message" picker on top of the same panel; the engine and
   the normalization display are identical either way, which is the property Q7.6.6 actually asks for.
8. **`SubjectPattern` on a supplier matcher stays a case-insensitive substring** — change #1's
   decision 1, revisited here as promised and left alone. The measurement says sender domain does
   most of the work, and every regex in this change is a *field* rule inside a validated template,
   where the compile-and-timeout apparatus already applies. Putting user regex on the matcher would
   put it on a path that runs against **every message in the mailbox**, which is the one place a
   pathological pattern is least affordable.
9. **A `DateFromField` rule may not name `DueDate` as its own source**, and its source must have a
   rule of its own. Both are save-time refusals rather than apply-time surprises.

---

## Risks

| Risk | Handling |
| --- | --- |
| **Friction #9 — the runtime guarantee is weaker than the compiler's.** A template is user data; `ValidTemplate` is the only thing standing between it and the engine | `ValidTemplate` has a private constructor and one producer. The exhaustiveness contract tests fail when a case is added without its mapper, editor and fixture |
| **Friction #19 — the whole calendar depends on `DateFromField`.** Without it, 88% of invoices arrive with no due date and change #7 ships an empty calendar | The invoice-management-platform fixture asserts exactly this rule, and it is a required task rather than an optional one |
| **A pathological regex hangs the scan** | Timeout on every construction, `NonBacktracking` where possible, `RuleTimedOut` as a named error, and a catastrophic-backtracking test that must complete inside the timeout |
| **Normalization is wrong in a way that only shows in production** | The three measured failure modes are each a test, and the test panel shows the normalized text — so a template is authored against the same text the scan sees, not a guess |
| **The split-column encoding of `FieldRule` loses a case silently** | Round-trip integration tests over every case, plus a reflection-driven exhaustiveness test |
| **A multi-row write commits half a template.** Three functions here write several rows; change #1 shipped exactly this and its fix is still untested | `inTransaction` on all three, and an atomicity test per function that fails if it is removed — *Carried over from change #1's review* |
| **A string → union conversion raises instead of refusing.** Four unions cross the top mapper as UI strings; change #1's one such conversion swallowed a write whole | Both mappers return `Result`; an unrecognised string is a named `TemplateError`. Asserted per case in the contract suite |
| **A rendering bug the E2E suite cannot see**, because the dialog is never rendered | Dialogs driven through `MudDialogProvider`; label-rendering and delete-one-of-two-identical asserted explicitly |
| **Multi-column layouts cannot be expressed at all** | Known and accepted: one supplier in the sample needs coordinates, consistency across its samples was 2 in 11, and `SameRowAsLabel` is recorded as the next rule kind with `ReadDocumentContent`'s `Word` type already waiting for it |
| **Two suppliers match one message and the wrong template runs** | `MultipleSuppliersMatched` is an error carrying *every* match, never a pick. It becomes a visible problem row in change #4 |
