# Requirements — Invoice templates

Change **#2 of 7**. Depends on **#1 (`invoice-ledger-foundation`)**. See
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md) for the decision
record and the measurements; question ids (`Q7.6.3`), findings (*Finding 4*) and friction numbers
(#9, #19) resolve there.

**What this change is for.** The number of suppliers cannot be known in advance, so how each
supplier's mail is read is **data the user types**, not a parser someone writes in F# (Q1.3/Q1.4).
This change delivers that rule model, the pure engine that runs it, and the page that edits it —
ask #6.

**Why it is the riskiest change in the series.** A user-editable rule engine sits awkwardly with
"types carry the rules" (friction #9): the guarantee the compiler normally gives has to move to a
validation boundary, so the tests carry more of the weight. It is also the change that must earn the
**12% → 39%** improvement (friction #19) that everything on the calendar depends on.

**What it is not.** No mail is read, no invoice is stored, no calendar is touched. The engine's input
is a `ScannedMessage`, and in this change the only thing that produces one is the test panel and the
test fixtures.

---

## Text normalization

Three failure modes were measured in the real mailbox, and **each one silently produces "rule matched
nothing" rather than an error** — a template that looks correct and cannot work (*Finding 4*).

### The contract

WHEN any rule is evaluated THE SYSTEM SHALL first apply a defined normalization to the text, and SHALL apply the identical normalization at authoring time and at scan time.
WHEN text is normalized THE SYSTEM SHALL apply Unicode NFKC.
WHEN text is normalized THE SYSTEM SHALL fold `U+00A0`, `U+2007`, `U+202F` and the other non-breaking and fixed-width space characters to a plain space.
WHEN text is normalized THE SYSTEM SHALL collapse runs of spaces and tabs to a single space, and strip leading and trailing whitespace from each line.
WHEN a line is a wrapped continuation of its predecessor **within the same block** THE SYSTEM SHALL join it to that predecessor.
WHEN a line would be joined across a block boundary THE SYSTEM SHALL NOT join it, because `LinesAfterLabel` depends on the block structure the boundary marks.
WHEN text is normalized THE SYSTEM SHALL drop empty lines before any line offset is applied, so `LinesAfterLabel(label, 1)` means "the next line with content".
WHEN a text line is produced by any reader THE SYSTEM SHALL carry the index of the block it came from, so the join rule has a boundary to respect.

### Proving it

WHEN a label separated from its value by a non-breaking space is matched THE SYSTEM SHALL find the value.
WHEN a label hard-wrapped across two lines is matched THE SYSTEM SHALL find the value.
WHEN a label is separated from its value by one or more blank lines THE SYSTEM SHALL find the value at offset 1.
WHEN a letter-spaced heading such as `TA X INVOICE` is present THE SYSTEM SHALL make the raw text visible to the template author, so the mismatch is diagnosable even though normalization does not repair it.

---

## The rule model

### Rule kinds

WHEN a template field rule is defined THE SYSTEM SHALL offer exactly these seven kinds: `AfterLabel`, `LinesAfterLabel`, `RegexCapture`, `FixedValue`, `SubjectCapture`, `AttachmentName`, `DateFromField` (Q7.6.1).
WHEN `AfterLabel` is applied THE SYSTEM SHALL return the remainder of the first normalized line containing the label, after the label.
WHEN `LinesAfterLabel` is applied THE SYSTEM SHALL find the label in the normalized text — joined lines included, so a hard-wrapped label is still found — and SHALL count the offset over the lines **as the document laid them out**, starting from the laid-out line the label ends on.
WHEN a value on its own line would be joined to its label as a wrapped continuation THE SYSTEM SHALL still return that value at offset 1, because whether a template works must not depend on the case of the first character of a value its author does not control.
WHEN `RegexCapture` is applied THE SYSTEM SHALL return the first capture group of the first match.
WHEN `FixedValue` is applied THE SYSTEM SHALL return that value without consulting the text at all.
WHEN `SubjectCapture` is applied THE SYSTEM SHALL run its pattern against the message subject, not the document body.
WHEN `AttachmentName` is applied THE SYSTEM SHALL run its pattern against the attachment's filename, not its content.
WHEN `DateFromField` is applied THE SYSTEM SHALL derive the date by adding the **supplier's** payment term to the date the named source field yielded.
WHEN a rule finds nothing THE SYSTEM SHALL report which field and which rule found nothing, never a default or an empty value silently substituted.

### Target fields and parse hints

WHEN a template targets a field THE SYSTEM SHALL offer exactly `Reference`, `Amount`, `Currency`, `IssueDate` and `DueDate`.
WHEN a template is defined THE SYSTEM SHALL allow **a different rule kind per field** — one supplier's document states the reference label-above-value and the due date inline, in the same document.
WHEN a value is parsed as a date THE SYSTEM SHALL use an **explicit format string** supplied by the template, and SHALL NEVER fall back to ambient-culture parsing.
WHEN a value is parsed as money THE SYSTEM SHALL use the decimal separator the template states, and SHALL strip currency symbols, thousands separators and surrounding whitespace before parsing.
WHEN a value is parsed as text THE SYSTEM SHALL return it as normalized, with no further interpretation.

### Document parts

WHEN a template is defined THE SYSTEM SHALL state which part of the message it applies to — the body, an attachment of a stated format, or any part.
WHEN a template is applied THE SYSTEM SHALL consider only the parts its document part selects.

### Validation — the boundary that replaces the compiler

WHEN a user saves a template THE SYSTEM SHALL validate it **at that moment**, not when a scan next runs.
WHEN a template's regular expression does not compile THE SYSTEM SHALL refuse the save with `PatternInvalid` carrying the reason.
WHEN a template's date format is not a valid format string THE SYSTEM SHALL refuse the save with `DateFormatInvalid` carrying the reason.
WHEN a template has no rule for `Reference`, `Amount` or `Currency` THE SYSTEM SHALL refuse the save with `RequiredFieldHasNoRule` naming the field.
WHEN a template has a `DateFromField` rule whose source field has no rule of its own THE SYSTEM SHALL refuse the save, because the derivation could never succeed.
WHEN a template has a `DateFromField` rule naming a non-date source field THE SYSTEM SHALL refuse the save.
WHEN a template has two rules targeting the same field THE SYSTEM SHALL refuse the save, because which one wins would be invisible.
WHEN a template passes validation THE SYSTEM SHALL produce a `ValidTemplate`, and the engine SHALL accept nothing else.
WHEN a `ValidTemplate` is constructed THE SYSTEM SHALL do so only by validating an unvalidated one — there is no other constructor.

### Regex safety

WHEN any regular expression from a template is constructed THE SYSTEM SHALL give it a match timeout.
WHEN a regular expression can be constructed with `RegexOptions.NonBacktracking` THE SYSTEM SHALL do so.
WHEN a pattern uses a construct `NonBacktracking` does not support THE SYSTEM SHALL fall back to a backtracking engine **with the timeout still applied**, and SHALL tell the user at save time that the pattern is on the slower path.
WHEN a rule's match exceeds the timeout THE SYSTEM SHALL fail **that rule** with `RuleTimedOut` naming the field and the template, and SHALL allow the rest of the scan to finish.
WHEN a rule times out THE SYSTEM SHALL NOT block the user interface.

---

## Supplier matching

WHEN a message is matched against the stored suppliers THE SYSTEM SHALL match on a sender address, a sender domain or a subject pattern, treating a supplier's rules as alternatives (Q7.6.5).
WHEN exactly one supplier matches a message THE SYSTEM SHALL return that supplier.
WHEN no supplier matches a message THE SYSTEM SHALL return `SupplierNotRecognised` carrying the sender, never a guess.
WHEN two or more suppliers match one message THE SYSTEM SHALL return `MultipleSuppliersMatched` carrying every matching supplier (Q7.6.4) — silently picking one is how a month of invoices ends up filed under the wrong supplier.
WHEN a supplier has no match rules THE SYSTEM SHALL never match it.
WHEN a sender address or domain is compared THE SYSTEM SHALL compare case-insensitively.

---

## Applying a template

### One template

WHEN a template is applied to a message THE SYSTEM SHALL run each field rule against the normalized text of the parts the template selects, parse each result with that rule's hint, and return the extracted invoice.
WHEN a required field's rule finds nothing THE SYSTEM SHALL return `TemplateMatchedNothing` naming the field.
WHEN a value cannot be parsed with its hint THE SYSTEM SHALL return an error naming the field, the raw text and the format expected — never a zero, a default date, or a silently dropped field.
WHEN `DueDate` has no rule and no derivation THE SYSTEM SHALL return the extracted invoice **without a due date**, because an invoice with no due date is still an invoice (Q1.10).
WHEN the engine runs THE SYSTEM SHALL perform no I/O, read no clock and generate no randomness — it is a pure function of a template, a payment term and a message.

### Several templates for one supplier

WHEN a supplier has several templates THE SYSTEM SHALL hold them in a user-chosen order.
WHEN a message is processed THE SYSTEM SHALL filter that supplier's templates to those whose document part matches, try them in order, and take the **first that yields every required field** (Q7.6.3).
WHEN a template produces an invoice THE SYSTEM SHALL record which template produced it.
WHEN no template yields every required field THE SYSTEM SHALL report the reason from the **last** template tried, so the diagnostic names a real failure rather than "nothing worked".
WHEN a supplier has no template for the parts a message carries THE SYSTEM SHALL return `NoTemplateForSupplier` carrying the supplier.

---

## Persistence

WHEN the migration runner is applied to an empty database THE SYSTEM SHALL create an `InvoiceTemplates` table with an identity primary key, a supplier-id foreign key, a name, the document part it applies to, and an ordering position.
WHEN the migration runner is applied to an empty database THE SYSTEM SHALL create a `TemplateFieldRules` table with an identity primary key, a template-id foreign key, a target field, a rule kind, the rule's pattern or label, a rule offset, and a parse hint.
WHEN a template is deleted THE SYSTEM SHALL delete its field rules.
WHEN a supplier is deleted THE SYSTEM SHALL delete its templates and their field rules.
WHEN a template is persisted THE SYSTEM SHALL store it as **relational rows**, never as a serialised blob in one column, so an export is a read and a `dotnet clean` cannot destroy a morning's work (Q5.10).
WHEN a rule kind is added in future THE SYSTEM SHALL gain a column, not a serialised field smuggled into an existing one.
WHEN each new migration's `Down()` is run THE SYSTEM SHALL remove exactly what its `Up()` created.

### Writes are all-or-nothing

Saving a template writes several rows — the template, then one per field rule — and reordering writes
one per template. Change #1 shipped the un-transactioned form of exactly this, so it is stated as a
requirement rather than left to the store's implementation.

WHEN a template is saved and any statement in that save fails THE SYSTEM SHALL store no part of it, leaving neither a template row without its rules nor a rule set without its template.
WHEN a template is edited and any statement in that edit fails THE SYSTEM SHALL leave the stored template exactly as it was before the edit, with its original rule set intact.
WHEN templates are reordered and any statement in that reorder fails THE SYSTEM SHALL leave the original order intact, never a partially applied one.
WHEN a write fails partway THE SYSTEM SHALL report the failure to the caller, and the state the caller then reads back SHALL agree with what it was told.

---

## User interface

### Templates page

WHEN a user opens a supplier's row on the suppliers page THE SYSTEM SHALL navigate to that supplier's templates.
WHEN a user navigates to a supplier's templates THE SYSTEM SHALL display the supplier's name, its payment term, and its templates in their stored order.
WHEN a user adds a template THE SYSTEM SHALL open an editor for its name, its document part, and its field rules.
WHEN a user edits a field rule THE SYSTEM SHALL offer an editor appropriate to the rule kind chosen — a label box for `AfterLabel`, a label box and an offset for `LinesAfterLabel`, a fixed value box for `FixedValue`, a source-field picker for `DateFromField`, a pattern box for the three pattern kinds.
WHEN a user reorders a supplier's templates THE SYSTEM SHALL persist the new order and use it when trying templates.
WHEN a user saves a template that fails validation THE SYSTEM SHALL display the specific reason and SHALL NOT store it.
WHEN a user deletes a template THE SYSTEM SHALL ask for confirmation, then delete it and its rules.
WHEN a template operation fails THE SYSTEM SHALL display the message in a `MudAlert`, and clear it on the next success.

### The test panel

WHEN a user opens the test panel THE SYSTEM SHALL accept sample text, a sample subject and a sample attachment filename, and run the template against them.
WHEN the test panel displays the text THE SYSTEM SHALL show it **after normalization** (Q7.6.6) — three of the measured failure modes are silent non-matches, and a panel showing raw text would cheerfully agree with a template that cannot work.
WHEN the test panel displays the text THE SYSTEM SHALL also make the **raw** text available, so a letter-spaced heading or an unexpected character is diagnosable.
WHEN the test panel runs a template THE SYSTEM SHALL show, per field, which rule ran, what it extracted, what it parsed to, and — where it failed — why.
WHEN the test panel runs a template with a `DateFromField` rule THE SYSTEM SHALL show both the source date it used and the derived due date, with the payment term it applied.
WHEN a rule in the test panel times out THE SYSTEM SHALL report the timeout against that rule and leave the rest of the panel usable.
WHEN the test panel is used THE SYSTEM SHALL run the **same** engine a scan runs, not a reimplementation of it.

---

## Testing

### The measured fixtures

WHEN this change is complete THE SYSTEM SHALL carry the **four worked templates** from the measured mailbox as test fixtures, each asserting every field it claims to extract.
WHEN the water utility's two templates are tested THE SYSTEM SHALL prove that a message matching the second but not the first yields an invoice, because that is what "several templates per supplier" was measured to be for.
WHEN the invoice-management platform's template is tested THE SYSTEM SHALL prove `DateFromField` produces the due date its documents never state — the rule that carries extraction from 12% to 39%.
WHEN a fixture is committed THE SYSTEM SHALL contain no real amount, reference, account number, address or supplier name — layout shapes only, values synthetic.

### Levels

WHEN a domain function is added or changed THE SYSTEM SHALL have a unit test written **before** the implementation.
WHEN the rule engine is tested THE SYSTEM SHALL use table-driven cases over `(template, message) → expected fields`, asserting every field of the success output and the exact error case with its payload on failure.
WHEN a constrained type gains a `create` THE SYSTEM SHALL have a test per rule with the rejection reason asserted.
WHEN the template store is tested THE SYSTEM SHALL run against a real SQLite database in a fresh temp file per test, with the schema built by `MigrationSetup.setupMigrations`.
WHEN each migration is added THE SYSTEM SHALL have `Up`/`Down` tests, and a test that deleting a supplier removes its templates and their rules.
WHEN a dependency function type is published THE SYSTEM SHALL have one shared contract suite run against the real adapter **and** every fake.
WHEN both mappers are added THE SYSTEM SHALL have contract tests asserting them field-for-field in both directions, including every rule kind, every target field and every parse hint.
WHEN a rule kind is added in future THE SYSTEM SHALL fail an existing exhaustiveness test until its mapper, its editor and its fixture exist.
WHEN the templates flow is complete THE SYSTEM SHALL have an E2E test covering add, edit, reorder, delete, a validation failure with nothing logged, and a test-panel run showing normalized text.
WHEN a test is added THE SYSTEM SHALL tag it with its level.

### Gate

WHEN this change is complete THE SYSTEM SHALL build the whole solution with zero errors and pass the whole suite with zero failures and zero skips.

---

## Edge cases

WHEN a label appears more than once in a document THE SYSTEM SHALL use the first occurrence, and the test panel SHALL show which line it took.
WHEN `LinesAfterLabel` is given an offset that runs past the end of the block THE SYSTEM SHALL report that the rule found nothing, not an index error.
WHEN a `LinesAfterLabel` offset is negative or greater than 20 THE SYSTEM SHALL refuse the save.
WHEN an `AfterLabel` or `LinesAfterLabel` label is empty, whitespace-only or absent THE SYSTEM SHALL refuse the save, because such a label matches the first line of every document and stores the whole of it.
WHEN a template scoped to the message body carries an `AttachmentName` rule THE SYSTEM SHALL refuse the save, because that rule reads a list of filenames the selector leaves empty and the field would be silently absent on every message.
WHEN a derived due date would fall outside the range of representable dates THE SYSTEM SHALL report that, naming the template, the issue date and the payment term — never raise out of the workflow.
WHEN a regular expression compiles but has no capture group THE SYSTEM SHALL refuse the save, because `RegexCapture` returns the first capture group.
WHEN an amount is written with a leading currency symbol, a thousands separator, or a trailing `CR`/`DR` THE SYSTEM SHALL parse the number and ignore the decoration.
WHEN an amount is negative or zero THE SYSTEM SHALL extract it as found — deciding whether a credit note is an invoice is not this change's job.
WHEN a two-digit year is parsed THE SYSTEM SHALL resolve it by the explicit format string given, and the templates page SHALL warn that `d/M/yy` is ambiguous.
WHEN a date is stated as `02/08/2016` and the template says `d/M/yyyy` THE SYSTEM SHALL read 2 August, and a template saying `M/d/yyyy` SHALL read 8 February — the format string is the only thing that decides, and there is a test for each.
WHEN an invoice reference is printed in space-separated groups THE SYSTEM SHALL fold the internal whitespace, so the same reference read from a PDF and from an attachment filename is one value, not two.
WHEN a message carries several attachments THE SYSTEM SHALL apply an attachment-part template to each in turn and take the first that yields every required field.
WHEN a supplier has no templates at all THE SYSTEM SHALL display the page with an empty list and an invitation to add one, not an error.
WHEN a template's document part selects a part the message does not carry THE SYSTEM SHALL skip that template rather than fail.

---

## Out of scope

- **Reading real mail.** `ScannedMessage` is produced by the test panel and by fixtures. Change #3 produces real ones; change #4 connects them.
- **The four document readers.** Change #4.
- **Storing invoices.** Nothing here writes an invoice; the engine returns a value.
- **A coordinate-based rule** (`SameRowAsLabel`). Recorded as the known next rule kind, with one measured supplier waiting for it. One supplier in the sample needs it and a coordinate rule is materially harder to explain in an editor.
- **Template export / import.** The model stays serialisable so this is a read and a write later (Q5.10).
- **A reprocess-this-supplier button.** A follow-up; the persisted problem rows added in change #4 are what make it cheap.
