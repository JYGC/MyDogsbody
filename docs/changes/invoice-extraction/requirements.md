# Requirements — Invoice extraction

Change **#4 of 7**. Depends on **#1, #2 and #3**. See
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md) for the decision
record and the measurements; question ids (`Q1.9`), findings (*Finding 1*) and friction numbers
(#7, #8, #14, #15, #17) resolve there.

**What this change is for.** Everything before it built a part. This one joins them: mail from
change #3, templates from change #2, suppliers from change #1, and the four document readers that
turn an attachment into text. The output is a **ledger** — invoices that persist, so you read your
mail once and keep what you found rather than re-deriving it on every glance. Ask #2.

**It is the largest change after #2**, and §4 named it the one to split first if it gets unwieldy.
The designated split is the scan-window apparatus — see `tasks.md` → *If this change gets too large*.

**What it is not.** No calendar, no Google, no sync. An invoice here is a stored fact with a due date
that may or may not exist.

---

## Document readers

### One capability, four readers

WHEN the domain needs a document read THE SYSTEM SHALL declare **one** dependency function type taking a document's bytes and returning text lines, satisfied by a reader chosen per format at the composition root.
WHEN a reader is bound THE SYSTEM SHALL do so once, in one place, for all four formats, so adding a fifth format never touches a workflow.
WHEN a document is read THE SYSTEM SHALL return text lines each carrying the index of the block they came from, so the normalization contract from change #2 has a boundary to respect.
WHEN the format of an attachment is decided THE SYSTEM SHALL decide it by **filename extension**, never by declared content type — 155 of 644 PDFs in the measured mailbox declare `application/octet-stream` and 4 declare `application/.pdf`, so dispatching on the declared type would misroute a quarter of them.

### PDF

WHEN a PDF attachment is read THE SYSTEM SHALL extract its text layer.
WHEN a PDF has no extractable text layer THE SYSTEM SHALL report it as a problem naming that cause, and SHALL NOT attempt optical character recognition — 94.7% of the measured PDFs have a text layer and only 1.6% do not.
WHEN a PDF cannot be opened at all THE SYSTEM SHALL report it as a problem naming that cause and continue the scan.
WHEN the existing coordinate-bearing document reader is used THE SYSTEM SHALL continue to work unchanged — the two capabilities coexist, and the same PDF adapter satisfies both.

### Word

WHEN a `.docx` attachment is read THE SYSTEM SHALL extract its text.
WHEN a legacy binary `.doc` attachment is encountered THE SYSTEM SHALL report an unsupported-format problem **naming the format**, and SHALL NOT silently skip it — silence looks identical to "this supplier sends nothing", and you would never learn the difference.

### Plain text and email bodies

WHEN a plain-text attachment is read THE SYSTEM SHALL decode it and split it into lines.
WHEN a message body is read and the message carries both a plain-text and an HTML alternative THE SYSTEM SHALL prefer the alternative that **preserves block structure**, which in practice means the HTML — the measurement found the plain-text alternative had already destroyed the label-to-value adjacency by wrapping.
WHEN an HTML body is read THE SYSTEM SHALL derive block boundaries from its structure — a table cell and a paragraph are blocks — rather than from line breaks.
WHEN a message carries only one body alternative THE SYSTEM SHALL use it.

### Unsupported formats

WHEN an attachment's format has no reader THE SYSTEM SHALL report a problem stating **which** format arrived, so the question of whether to build a reader for it can later be answered from data. The measured mailbox contains 114 `.xlsx` attachments against 1 `.docx`.

---

## The scan

### The window

WHEN the scan window is modelled THE SYSTEM SHALL model it as a constrained **number of days**, and the set of available windows as **rows in the database**, not as a closed union.
WHEN a scan window is created from fewer than 1 day or more than 3650 days THE SYSTEM SHALL reject it with a reason naming the bound — a typo guard, so 14000 typed for 1400 is refused rather than walking the whole store.
WHEN the scan-windows table is created THE SYSTEM SHALL seed it with 7, 14, 30, 90 and 180 days **from the migration**, and that migration's `Down()` SHALL remove them.
WHEN a user adds a scan window THE SYSTEM SHALL accept any value inside the bounds that is not already present, and SHALL take effect **without a rebuild**.
WHEN a user adds a scan window that already exists THE SYSTEM SHALL refuse it with a named error.
WHEN a user deletes a scan window THE SYSTEM SHALL remove it, including a seeded one.
WHEN a user deletes the **last remaining** scan window THE SYSTEM SHALL refuse with a named domain error, so the picker can never be empty and no component needs an "if the list is empty" branch.
WHEN a scan window is offered for editing THE SYSTEM SHALL NOT offer one — a window is one number, so changing it is a delete and an add, and an edit would be a second path to the same duplicate check.

### Remembering the choice

WHEN a user selects a scan window THE SYSTEM SHALL persist the choice **as a number of days** in the main database, not as a foreign key to the window row.
WHEN the invoices page opens against a fresh database THE SYSTEM SHALL open on **14 days**.
WHEN the invoices page opens and a choice was remembered and that window still exists THE SYSTEM SHALL open on it.
WHEN the invoices page opens and the remembered window has since been deleted THE SYSTEM SHALL open on 14 days, or — if 14 has itself been deleted — on the shortest window still present.
WHEN the fallback is decided THE SYSTEM SHALL decide it in one place, so the rule cannot end up half in a module creator and half in a mapper.
WHEN the settings are stored THE SYSTEM SHALL use a **typed single-row table**, one column per setting, extended by migration — not a key/value store whose every value is a string parsed at the point of use.

### The cutoff

WHEN a cutoff is computed THE SYSTEM SHALL compute it as the **start of the day** the given number of days ago, so the same window scanned at 09:00 and at 17:00 covers the same mail.
WHEN a workflow needs the current time THE SYSTEM SHALL receive it as a dependency function, and SHALL NOT read a clock directly.
WHEN the cutoff arithmetic is tested THE SYSTEM SHALL pin a fixed instant and assert an exact date.
WHEN the window is described on screen THE SYSTEM SHALL say what it measures — "mail received in the last 90 days", not "90 days" — because a bare number is exactly where someone assumes it means due dates.

### Running a scan

WHEN a scan runs THE SYSTEM SHALL read only the selected mail account, and SHALL refuse with a named error when none is selected.
WHEN a scan runs THE SYSTEM SHALL pass the computed cutoff to the mail reader so it stops reading rather than reading everything and discarding.
WHEN a scan reads a message THE SYSTEM SHALL flatten it and its attachments to text, match a supplier, apply that supplier's templates in order, validate the result, and store it.
WHEN a scan encounters a message that yields no invoice THE SYSTEM SHALL record the reason against that message and **continue** — never silent, never fatal to the scan.
WHEN a scan completes THE SYSTEM SHALL return both the invoices found and the problems recorded, as two lists.
WHEN a user changes the scan window THE SYSTEM SHALL persist the choice and reload the stored ledger for that window, and SHALL NOT read the mailbox again. *(Phase 15: the Phase 12 measurement put a full scan at ~60 s whatever the window — the cost is reading every folder, not the cutoff — so the immediate rescan of the earlier draft is dropped.)*
WHEN a user asks to scan (the explicit "Scan now", and the initial page load) THE SYSTEM SHALL read the mailbox for the current window and refresh the ledger from the result.
WHEN a scan runs THE SYSTEM SHALL NOT block the user interface.
WHEN a scan is measured against the real mail store THE SYSTEM SHALL have its duration recorded in the change description — if changing the window costs seconds, an explicit Refresh button comes back in place of the immediate rescan. *(Measured at ~60 s; the Refresh button — "Scan now" — is Phase 15.)*

---

## The ledger

### Identity

WHEN two scans find the same invoice THE SYSTEM SHALL agree they are the same by **supplier and invoice reference**.
WHEN invoices are stored THE SYSTEM SHALL place a unique index on that pair, so the database refuses a duplicate even when the code is wrong.
WHEN an invoice reference is created THE SYSTEM SHALL fold internal whitespace, so a reference printed in space-separated groups and the same digits in an attachment filename are one value, not two.
WHEN an overlapping window is rescanned THE SYSTEM SHALL update the existing invoice rather than adding a second one.
WHEN an invoice is stored THE SYSTEM SHALL record the source message it came from, and **nothing else about its provenance** — no account name, no folder name. Thunderbird's vocabulary stops at the integration boundary.
WHEN an invoice is stored THE SYSTEM SHALL record which template produced it.

### Fields

WHEN an invoice is validated THE SYSTEM SHALL require a supplier, an invoice reference, an amount and a currency.
WHEN an invoice has no due date THE SYSTEM SHALL store it and list it anyway, marked with the reason it cannot go on a calendar — the record of the invoice survives whether or not it can become an event.
WHEN an invoice has an issue date THE SYSTEM SHALL store it.
WHEN a value fails validation THE SYSTEM SHALL record a problem against the message naming the field and the raw value, and SHALL NOT store a partial invoice.

### Correcting by hand

WHEN a user deletes an invoice THE SYSTEM SHALL remove it from the ledger.
WHEN a user attempts to edit an invoice THE SYSTEM SHALL offer no such action — delete yes, edit no, so there is no path by which a typed-in value is silently overwritten by the next scan.
WHEN a user deletes an invoice THE SYSTEM SHALL record a **tombstone** on the supplier-and-reference key, so the next scan does not put it back.
WHEN a scan encounters an invoice whose key is tombstoned THE SYSTEM SHALL skip it.
WHEN tombstones exist THE SYSTEM SHALL show them on screen with the date each was created, and SHALL offer an un-delete.
WHEN a tombstone is removed THE SYSTEM SHALL allow the next scan to store that invoice again.

### Problems

WHEN a message yields no invoice THE SYSTEM SHALL persist a problem row keyed by source message id, carrying the cause and enough detail to act on it.
WHEN a message that previously produced a problem later yields an invoice THE SYSTEM SHALL clear that problem row.
WHEN problems are recorded THE SYSTEM SHALL distinguish the causes: no supplier matched; **two or more suppliers matched**; no template for that supplier; a rule found nothing; an attachment was unreadable; the attachment's format is unsupported; a value could not be parsed; a rule timed out.
WHEN problems are displayed THE SYSTEM SHALL show the message's sender, subject and date alongside the cause, because a message id alone is not actionable.
WHEN the same supplier produces the same problem every month THE SYSTEM SHALL make that visible, so it can be recognised as deliberate rather than wondered about — council rates receipts and property-management owner statements both parse cleanly and are not invoices.

---

## Persistence

WHEN the migration runner is applied THE SYSTEM SHALL create tables for invoices, scan problems, invoice tombstones, scan windows and invoice settings.
WHEN the scan-windows table is created THE SYSTEM SHALL make its day value unique.
WHEN the invoice-settings table is created THE SYSTEM SHALL fix its primary key at a single row.
WHEN a migration seeds rows THE SYSTEM SHALL insert them in `Up()` and remove them in `Down()`, and the change description SHALL state that a migration file now carries data as well as structure — every migration before this one created schema and nothing else.
WHEN a seeded scan window is deleted by a user THE SYSTEM SHALL NOT restore it on the next migration run — that is the correct behaviour for a value the user chose to remove, and is why a fallback constant exists rather than the code assuming 14 is present.
WHEN store functions are written THE SYSTEM SHALL place them in the main database project, keeping the outer-ring shape and adding one action-name entry per function.
WHEN this change is complete THE SYSTEM SHALL still have exactly **two** mapping points per feature — record to domain at the bottom, domain to UI record at the top.

---

## User interface

### Invoices page

WHEN a user navigates to `/invoices` THE SYSTEM SHALL display the scan-window picker, the invoice table, and the number of invoices and the window they fall in, stated above the table.
WHEN the scan-window picker is rendered THE SYSTEM SHALL render whatever windows the store holds, and SHALL hold no list of its own — adding a sixth window is done on screen and takes effect without a rebuild.
WHEN the picker is rendered THE SYSTEM SHALL open on the resolved remembered choice, never on a literal value written into a component.
WHEN a user changes the window THE SYSTEM SHALL persist the choice and reload the stored ledger for that window (Phase 15 — not a mailbox scan; see the *Scanning* section).
WHEN the `/invoices` page is rendered THE SYSTEM SHALL show a "Scan now" control that reads the mailbox for the current window, disabled while a scan or reload is in flight.
WHEN the window is narrowed THE SYSTEM SHALL hide invoices outside it and SHALL NOT delete them — narrowing hides, it does not forget; widening the window again brings them back with no scan.
WHEN an invoice has no due date THE SYSTEM SHALL show it greyed with the reason it cannot go on a calendar.
WHEN a user deletes an invoice THE SYSTEM SHALL ask for confirmation, then delete it and reload.
WHEN a user opens the problems view THE SYSTEM SHALL list the messages that yielded nothing, with sender, subject, date and cause.
WHEN a user opens the tombstones view THE SYSTEM SHALL list deleted invoices with the date each was deleted, and offer an un-delete.
WHEN an operation fails THE SYSTEM SHALL display the message in a `MudAlert`, and clear it on the next success.
WHEN the table holds hundreds of rows THE SYSTEM SHALL page them client-side; no server-side paging is required in the first pass.

### Scan windows page

WHEN a user navigates to `/settings/scan-windows` THE SYSTEM SHALL list the windows in days with the currently remembered one marked.
WHEN a user adds a window THE SYSTEM SHALL validate it and add it, or refuse with the specific reason.
WHEN a user deletes a window THE SYSTEM SHALL delete it, unless it is the last one.
WHEN only one window remains THE SYSTEM SHALL show its delete action as unavailable, with the reason.

---

## Testing

### Levels

WHEN a domain function is added THE SYSTEM SHALL have a unit test written **before** the implementation, asserting every field of the success output and the exact error case with its payload.
WHEN cutoff arithmetic is tested THE SYSTEM SHALL supply a fixed instant through the clock dependency and assert an exact date, with no mail store anywhere near it.
WHEN the window-resolution rule is tested THE SYSTEM SHALL cover all three cases, including **the remembered window having been deleted** — the case nobody thinks to try by hand.
WHEN the clock dependency is published THE SYSTEM SHALL state explicitly how its contract suite is satisfied, rather than quietly having none: the suite asserts the properties any clock must hold, and the arithmetic that has actual logic is unit-tested against fixed instants.
WHEN the document readers are tested THE SYSTEM SHALL run against committed fixture documents, including a PDF with no text layer, a PDF that cannot be opened, a legacy `.doc`, an `.xlsx`, and a message carrying both body alternatives.
WHEN the ledger is tested THE SYSTEM SHALL run against a real SQLite database in a fresh temp file per test with the schema built by the migration runner.
WHEN each migration is added THE SYSTEM SHALL have `Up`/`Down` tests, and the seeding migration SHALL have a test that `Down()` removes the seeded rows.
WHEN a dependency function type is published THE SYSTEM SHALL have one shared contract suite run against the real adapter **and** every fake.
WHEN the invoices flow is complete THE SYSTEM SHALL have an E2E test covering a scan producing invoices, a window change that filters the ledger without a scan, "Scan now" reading the mailbox, a per-row delete producing a tombstone, an un-delete, and a problem row appearing for a message that yields nothing.
WHEN a test is added THE SYSTEM SHALL tag it with its level.

### Specific proofs this change owes

WHEN a rescan of an overlapping window runs THE SYSTEM SHALL leave the ledger with the same number of invoices, updated rather than duplicated.
WHEN an invoice is deleted and a covering window is rescanned THE SYSTEM SHALL NOT restore it.
WHEN a tombstone is removed and a covering window is rescanned THE SYSTEM SHALL restore it.
WHEN a scan runs twice within one day THE SYSTEM SHALL compute the same cutoff both times.
WHEN a message produces a problem and its template is later fixed THE SYSTEM SHALL clear the problem row on the next scan of a covering window.

### Gate

WHEN this change is complete THE SYSTEM SHALL build the whole solution with zero errors and pass the whole suite with zero failures and zero skips.
WHEN this change is complete THE SYSTEM SHALL have had a real scan measured against the real mail store, with the duration recorded.

---

## Edge cases

WHEN one message carries two invoices THE SYSTEM SHALL store both, because the source message id is traceability and not the key.
WHEN a scan finds an invoice whose supplier has since been deleted THE SYSTEM SHALL report it as a problem rather than storing an invoice with no supplier.
WHEN an attachment is empty or zero bytes THE SYSTEM SHALL report it as unreadable rather than as text that matched nothing.
WHEN an attachment is very large THE SYSTEM SHALL read it without loading the whole folder into memory.
WHEN a supplier's payment term changes THE SYSTEM SHALL apply the new term on the next scan and update the derived due dates of invoices it rescans; invoices outside the window are unaffected until they are rescanned.
WHEN a template changes THE SYSTEM SHALL leave existing invoices alone; the next scan of a covering window updates them through the natural key.
WHEN a scan window is deleted while it is the selected one THE SYSTEM SHALL fall back on the next page load rather than failing.
WHEN two scans run concurrently THE SYSTEM SHALL not corrupt the ledger — the unique index is the backstop.
WHEN the mail account is switched THE SYSTEM SHALL leave existing invoices in place; an invoice outlives the integration it came from.
WHEN an amount is parsed as zero or negative THE SYSTEM SHALL store it as found.

---

## Out of scope

- **Anything calendar-shaped.** No Google, no events, no diff, no sync status. Change #7.
- **`UploadableInvoice`.** The stage type that makes "no due date cannot become an event" a compile-time fact belongs with the workflow that consumes it, in change #7.
- **A reprocess-this-supplier button.** A follow-up. The persisted problem rows this change adds are what make it cheap: they name exactly which messages to re-read after a template change, instead of a full pass over the whole store.
- **Editing an invoice by hand.** Delete only.
- **Server-side paging.** Hundreds of invoices are assumed; client-side paging is enough.
- **A reader for `.xlsx`.** None of the measured spreadsheets were invoices. The unsupported-format problem row names the format so this can be revisited from data.
- **A coordinate-based template rule.** Recorded in change #2 as the known next rule kind.
