# Tasks — Invoice templates

Change **#2 of 7**. Depends on **#1**. [`requirements.md`](requirements.md) ·
[`design.md`](design.md) · [decision record](../invoice-to-calendar/background.md)

**Branch: `change/invoice-templates`, cut from `main` once #1 has merged.** Everything in this file
lands on it, and it merges **only** when Phase 12 has passed in full — zero build errors, zero test
failures, zero skips, all four levels. No other change shares this branch, and none of this work
happens on `main`. This is one of the two largest changes in the series, which is exactly why it does
not share a diff with anything else.
See [background → *One branch per change*](../invoice-to-calendar/background.md#one-branch-per-change).

**The ordering rule, per task:** where a task produces production code, its unit test is written
first, run, and confirmed to fail *for the reason expected* before the implementation. Tasks marked
*(test-first)* carry production code.

**Reserved migration timestamps for this change: `20260810000002`–`20260810000003`.** Renumbered from
`20260809000003`–`…0004`: change #1 shipped a sixth migration (`20260810000001`, the case-insensitive
name index, PR #8) that sorts above the whole `20260809` block. See
[background → *Migration timestamps*](../invoice-to-calendar/background.md#migration-timestamps-reserved-across-the-series).

**Read [`design.md` → *Carried over from change #1's review*](design.md) before Phase 7.** Four
defects found reviewing change #1 recur in this change's code, three of them under green tests. The
tasks below name them where they land, but the reasoning is there.

**Build order note.** Phases 1–4 are pure domain work and can be done before any UI exists. That is
deliberate: this is the riskiest area in the series (friction #9) and the engine should be proven by
table-driven tests before a page is written against it.

---

## Phase 1 — Normalization (required, first)

Finding 4 says every one of these failures is **silent**. Nothing else in this change can be trusted
until normalization is correct, so it goes first.

- [x] **1.1** *(test-first)* `TextLine` and `DocumentFormat` added to
      `Domain/Documents/DocumentsTypes.fs`.
      *Outcome:* `TextLine` carries `BlockIndex`. No test of its own — type declarations.
- [x] **1.2** *(test-first)* `Domain/InvoiceTemplates/TextNormalization.fs`.
      Tests, one per clause: NFKC applied; `U+00A0`, `U+2007`, `U+202F` folded to a plain space;
      runs of spaces and tabs collapsed; each line trimmed; a wrapped continuation joined **within**
      a block; a continuation **not** joined across a block boundary; empty lines dropped before
      offsets apply. Plus one case whose result changes if two clauses are reordered, so the order
      is pinned.
      *Depends on:* 1.1.
- [x] **1.3** *(test-first)* The three **measured** failure modes, as their own tests:
      a label separated from its value by a non-breaking space matches;
      a label hard-wrapped across two lines matches;
      a label separated from its value by blank lines matches at offset 1.
      *Outcome:* each test is annotated with the supplier shape it came from. **These are the tests
      that stop a correct-looking template from silently never matching.**
      *Depends on:* 1.2.

## Phase 2 — The rule model (required)

- [x] **2.1** *(test-first)* `TemplateId`, `TemplateName` in
      `Domain/InvoiceTemplates/InvoiceTemplatesTypes.fs`.
      Tests: one accepted and one rejected value per rule, reason asserted.
- [x] **2.2** `DocumentPart`, `FieldRule`, `TargetField`, `ParseHint`, `TemplateFieldRule`,
      `UnvalidatedTemplate`, `ValidTemplate` (private constructor), `StoredTemplate`,
      `TemplateError`, and the **six** dependency function types — the five template ones plus
      `LoadSuppliersForTemplates`, this area's own supplier-loading type. It is declared here rather
      than reusing `Suppliers.LoadSuppliers`, which returns `Result<_, SupplierError>`: reusing it
      would put a `Result.mapError` at every call site and make one dependency type span two error
      DUs, owing a contract suite in both areas.
      *Outcome:* `ValidTemplate` exposes accessors and **no constructor**. Added to the `.fsproj` in
      the compile order in `design.md`.
      *Depends on:* 2.1, 1.1.
- [x] **2.3** *(test-first)* Regex compilation helper — `NonBacktracking` first, backtracking with
      the timeout as fallback, both carrying a 250 ms match timeout.
      Tests: a plain pattern compiles on the `NonBacktracking` path; a lookaround pattern compiles on
      the **fallback** path and reports that it did; a syntactically invalid pattern returns an
      error with the reason; a catastrophic-backtracking pattern (`(a+)+$` against a long
      non-matching input) **completes inside the timeout** rather than hanging.
      *Depends on:* 2.2.
- [x] **2.4** *(test-first)* `ValidateTemplateWorkflow` — the only door to `ValidTemplate`.
      Tests, one per refusal, each asserting the case **and its payload**: name invalid; pattern does
      not compile; pattern has no capture group; date format not a real format string; offset
      negative or over 20; a required field (`Reference`, `Amount`, `Currency`) has no rule; two
      rules for one field; a `DateFromField` whose source has no rule; a `DateFromField` naming a
      non-date source; a `DateFromField` naming `DueDate` itself. Plus the Ok path asserting the
      compiled patterns are present on the result.
      *Depends on:* 2.3.

## Phase 3 — Invoice-side types and matching (required)

- [x] **3.1** *(test-first)* `SourceMessageId`, `MessagePart`, `ScannedMessage`, `ExtractedInvoice`,
      `InvoiceError` in `Domain/Invoices/InvoicesTypes.fs`.
      Tests: `SourceMessageId.create` accepts and rejects per rule.
      *Outcome:* the file is created here and **extended** by change #4 — do not duplicate it.
      *Depends on:* 2.2.
- [x] **3.2** *(test-first)* `Invoices/MatchSupplierWorkflow.fs`.
      Tests: exactly one supplier matches → that supplier; a sender-domain rule matches an address in
      that domain; comparison is case-insensitive; a subject substring matches; no match →
      `SupplierNotRecognised` with the **sender** asserted; two matches → `MultipleSuppliersMatched`
      with **both** supplier ids asserted; a supplier with no rules never matches.
      *Depends on:* 3.1.

## Phase 4 — The engine (required) — the heart of the change

- [x] **4.1** *(test-first)* `Invoices/ApplyTemplateWorkflow.fs`, table-driven.
      Tests: one case per rule kind — `AfterLabel`, `LinesAfterLabel`, `RegexCapture`, `FixedValue`,
      `SubjectCapture`, `AttachmentName` — each asserting **every** output field; a label appearing
      twice takes the first; an offset past the end of a block reports the rule found nothing;
      `TemplateMatchedNothing` names the field; the workflow takes **no dependency parameters**.
      *Depends on:* 3.1, 2.4, 1.2.
- [x] **4.2** *(test-first)* Parse hints inside the engine.
      Tests: `AsMoney` strips a leading `$`, a thousands separator and a trailing `CR`/`DR`;
      `AsMoney` with a comma decimal separator; a negative and a zero amount extract as found;
      `AmountUnparseable` carries the raw text; `AsDate` with each of the four measured formats
      (`d MMM yyyy`, `d/M/yyyy`, `d/M/yy`, `MMM d, yyyy`); `DateUnparseable` carries the raw text and
      the format expected.
      **And the pair that matters most:** `02/08/2016` read with `d/M/yyyy` is 2 August;
      the same text read with `M/d/yyyy` is 8 February. Two tests, because this is a six-month error
      that silently lands an event on the wrong day.
      *Depends on:* 4.1.
- [x] **4.3** *(test-first)* `DateFromField` — **the 12% → 39% rule** (friction #19).
      Tests: `DateFromField IssueDate` with `PaymentTermDays 30` and an issue date of 14 July gives
      13 August; a term of 0 gives the issue date itself; the source field yielding nothing makes the
      due date absent rather than wrong; the term comes from the **supplier**, so two templates for
      one supplier derive the same due date from the same document.
      *Depends on:* 4.2.
- [x] **4.4** *(test-first)* `RuleTimedOut` at apply time.
      Tests: a pathological pattern inside a `ValidTemplate` fails **that rule** with `RuleTimedOut`
      carrying the template and field, inside the timeout; the rest of the extraction is unaffected.
      *Depends on:* 4.1, 2.3.
- [x] **4.5** *(test-first)* `Invoices/SelectTemplateWorkflow.fs`.
      Tests: templates tried in stored order and the first complete match wins; a message matching
      only the **second** template still yields an invoice; the winning `TemplateId` is on the
      result; when all fail the error is the one from the **last** template tried; a template whose
      document part the message does not carry is skipped, not failed; no matching template →
      `NoTemplateForSupplier` with the supplier asserted; several attachments are each tried in turn.
      *Depends on:* 4.1.
- [x] **4.6** *(test-first)* `InvoiceReference` internal-whitespace folding.
      Test: `"1234 5678 90"` from a PDF and `"1234567890"` from an attachment filename produce **one**
      value. Without this, the natural key makes one invoice into two ledger rows and two calendar
      events.
      *Note:* `InvoiceReference` itself is a change #4 type. Fold the whitespace **here**, where the
      two sources first meet, and have #4's `create` reuse it.
      *Depends on:* 4.1.

## Phase 5 — The measured fixtures (required)

- [x] **5.1** `Fixtures/MeasuredTemplates.fs` — the four worked templates as synthetic documents in
      the measured layouts. **Layout only: no real amount, reference, account number, address or
      supplier name.**
      *Depends on:* 4.5.
- [x] **5.2** *(test-first)* One test per fixture, asserting **every field it claims to extract**.
      Including: the invoice-management platform's `DateFromField` producing a due date its documents
      never state; the water utility's second template being reached when the first fails; the
      accounting platform using a different rule kind per field in one document; the
      `AttachmentName` variant; the `SubjectCapture` variant.
      *Outcome:* **this closes the gap the pre-proposal named as its one unproven thing** — until now
      these templates were exercised by a scratch script, never by `ApplyTemplateWorkflow`.
      *Depends on:* 5.1.

## Phase 6 — Template CRUD workflows (required)

- [x] **6.1** *(test-first)* `AddTemplateWorkflow`.
      Tests: Ok path with every field; `TemplateSupplierNotFound`; each validation refusal propagated
      with its payload; **`saveTemplate` never called** on any refusal; a new template's position is
      last in the supplier's order.
      *Depends on:* 2.4.
- [x] **6.2** *(test-first)* `EditTemplateWorkflow`. Tests: Ok path; `TemplateNotFound`; refusals
      propagated; `updateTemplate` never called on a refusal; the rule set is **replaced**, not merged.
- [x] **6.3** *(test-first)* `DeleteTemplateWorkflow`. Tests: Ok; `TemplateNotFound`; dependency not
      called on an unusable id.
- [x] **6.4** *(test-first)* `ListTemplatesWorkflow`. Tests: returned in `Position` order; empty list
      is `Ok []`, not an error.
- [x] **6.5** *(test-first)* Reorder. Tests: a new order persists; an order naming a template not
      belonging to that supplier is refused; an order omitting a template is refused.

## Phase 7 — Migrations and store (required)

- [x] **7.1** *(test-first)* `Migration_20260810000002_CreateInvoiceTemplatesTable.fs`.
      **`Execute.Sql` for the `CREATE TABLE`** — it carries a foreign key, and FluentMigrator's SQLite
      generator refuses `Create.ForeignKey` while the fluent builder cannot express an inline one.
      Fluent builder for the index and `Down()`. Copy
      `Migration_20260809000002_CreateSupplierMatchersTable.fs`.
      Tests: `MigrateUp` produces the expected columns; the `(SupplierId, Position)` index exists;
      **deleting a supplier deletes its templates**; `Down()` reverses it.
- [x] **7.2** *(test-first)* `Migration_20260810000003_CreateTemplateFieldRulesTable.fs`. Same
      `Execute.Sql` treatment — this table has a foreign key too.
      Tests: as above; the unique index on `(TemplateId, TargetField)` refuses a duplicate;
      **deleting a template deletes its rules**; deleting a **supplier** deletes templates *and*
      rules; `Down()` reverses it.
      *Depends on:* 7.1.
- [x] **7.3** *(test-first)* `TemplateRecordMappers.fs` — the bottom mapper, with the split-column
      encoding for `DocumentPart`, `FieldRule` and `ParseHint`.
      Tests: field-for-field both directions for **every** case of all four unions; an unrecognised
      stored string maps to an error rather than a default.
      *Depends on:* 2.2, 7.2.
- [x] **7.4** *(test-first)* `TemplateStore.fs` — `getForSupplier`, `insertOne`, `updateOne`,
      `deleteOne`, `reorder`. Outer-ring shape, `handleError`, `Result<_, MyDogsbodyException>`.
      **Copy four shapes from `SupplierStore.fs` rather than re-deriving them:** `runSync` (the
      Dapper.FSharp async-only bridge); `inTransaction`; plain parameterised SQL with
      `SELECT last_insert_rowid()` folded into the same command for the two identity inserts (the
      `insert { }` CE cannot resolve `excludeColumn` without a `for … in table do`); and `List.iter`
      rather than a CE `for` loop when writing N rule rows, because `HandleErrorBuilder` defines no
      `Combine`. Load a supplier's templates and **all** their rules in two queries grouped in
      memory — not one query per template.
      Tests *(Integration)*: round trip for **every** rule kind, target field and parse hint;
      reorder persists; both cascades fire.
      Tests *(Integration, atomicity)*: **one per multi-row write** — `insertOne`, `updateOne` and
      `reorder` each leave no trace when a statement partway through fails. A rule row violating the
      `(TemplateId, TargetField)` unique index is the cheapest trigger. **Each must fail if
      `inTransaction` is removed** — confirm that by removing it, watching the test fail, and putting
      it back. Change #1 shipped this defect and its fix is still unasserted; this is where that stops.
      Tests *(Unit)*: each error path asserts the declared `ActionNames` string, the message and a
      preserved inner exception.
      *Depends on:* 7.3.
- [x] **7.5** `ActionNames.MyDogsbody.Database.TemplateStore.*`, five entries.
      *Outcome:* the structural suite still passes.

## Phase 8 — Composition root (required)

- [x] **8.1** *(test-first)* `TemplateApiMappers.fs` — domain ⇄ UI, `toTemplateError`,
      `toMyDogsbodyException`.
      **Every string → union conversion returns `Result<_, TemplateError>` — none raises.** Four
      unions arrive from the UI as plain strings (`DocumentPart`, the `FieldRule` kind, `TargetField`,
      `ParseHint`), and an unrecognised value is a named refusal, never a default and never a
      `failwith`. Change #1's `toMatcherKind` raised, outside any `handleError` block, on a path the
      UI calls from `Async.Start` — no alert, no log, and the write silently never happened. Do not
      copy `CredentialApiMappers.toInfrastructure`: it is sound only because it takes a C# **enum**.
      Tests: mapper field-for-field both directions over every union case; **an unrecognised string
      per union returns its error case rather than raising**; each `TemplateError` case → its intended
      action and message, with the **expected/unexpected split** asserted (everything but
      `TemplateStoreFailed` wraps an `ApplicationException` and is not logged).
- [x] **8.2** *(test-first)* `TemplateApiFactory.createTemplateApi handleError databaseContext`,
      including a `TestTemplate` member that runs `ValidateTemplateWorkflow` then
      `ApplyTemplateWorkflow` over a pasted-text `ScannedMessage` and returns the per-field results.
      Tests *(Integration)*: each API member against a real temp database. No module-level I/O.
      *Depends on:* 7.4, 8.1.
- [x] **8.3** `ActionNames.MyDogsbody.Startup.TemplateApi.*`.
- [x] **8.4** `Startup.fs`: `templateApi` bound and registered.
      *Outcome:* `MainWindow.xaml.cs` unchanged.

## Phase 9 — UI (required)

- [x] **9.1** `MyDogsbody.UI.Types`: `TemplateUiType`, `FieldRuleUiType`, `TemplateTestResultUiType`,
      `TemplateApi`, `Modules/TemplatesBrowserModule.fs`. *(Landed with PR 5/composition-root — see
      that PR's description for why, mirroring change #1's SupplierApi.fs precedent.)*
- [x] **9.2** *(test-first)* `ModuleCreators/TemplatesBrowserModuleCreators.fs` — `cval`/`transact`,
      `startWork` first, write-then-reload.
      Tests: a successful add reloads; a failed save sets `ErrorAval` with the specific reason; a
      later success clears it; a reorder reloads.
- [x] **9.3** `Components/TemplatesComponents.fs` — the template editor dialog (a **class**
      inheriting `FunComponent`) and the per-kind rule editors.
      **Two traps that cost change #1, both in the rule list this dialog renders:** a `MudListItem`
      given both a `Text` property and child content renders one or the other, so put the label in the
      child content or every rule shows as an unlabelled button; and remove entries **by index**
      (`List.indexed` + `List.removeAt`), never by value — F# records compare structurally, so
      filtering by value deletes every identical rule at once, and two rules may legitimately be
      identical.
      **Build the rule editors in this order**, cheapest and most-used first:
      `LinesAfterLabel` → `AfterLabel` → `FixedValue` → `AttachmentName` → `SubjectCapture` →
      `DateFromField` → **`RegexCapture` last** (Q7.6.2 — it fired once in 1,199 candidates).
- [x] **9.4** `Pages/Settings/TemplatesPage.fs`, route
      `routeCif "/settings/suppliers/%s/templates"` (Fun.Blazor's parameterised-route form —
      `routeCi` alone has no parameter slot; confirmed against the real package, not assumed),
      registered in `Shell.fs`, reachable from the suppliers table row (`Href` link, no direct
      module reference needed between the two pages).
      *Depends on:* 9.3, 9.2.
- [x] **9.5** **The test panel** (Q7.6.6), with one disclosed simplification. Sample text, sample
      subject, sample attachment filename; shows the text **after normalization** with the raw
      pasted text alongside it; shows per field whether it succeeded and, for a failure, the
      reason.
      *Simplification:* "what it extracted" (raw) and "what it parsed to" (typed) are shown as the
      **same** value per field, not two distinct ones, because `ApplyTemplateWorkflow`'s `Result`
      only carries the final parsed value — the intermediate raw string is not exposed anywhere in
      its return type. Separating them properly means changing that workflow's signature (already
      merged in PR 3) to carry both, which is a real change belonging to its own task rather than a
      side effect of the UI. `DateFromField`'s source date / payment term / derived date are
      likewise not broken out separately — the derived value is shown, not its derivation.
      *Outcome:* it calls the **same** engine a scan calls — no reimplementation.
      *Depends on:* 9.4, 8.2.
- [x] **9.6** Reorder control on the templates list (up/down buttons per row, not drag-and-drop —
      task only asked for "a control", and MudBlazor's drag primitives would be a larger addition
      than this change's UI slice needs).
- [x] **9.7** Delete confirmation (`IDialogService.ShowMessageBox`, same idiom as Suppliers').

## Phase 10 — Contract suites (required)

- [x] **10.1** One shared suite per dependency function type — `LoadTemplatesForSupplier`,
      `SaveTemplate`, `UpdateTemplate`, `DeleteTemplate`, `ReorderTemplates` and
      `LoadSuppliersForTemplates` — real adapter **and** every fake. **`MemberData` source must be a
      public `let`.**
      `Contracts/TemplateDependencyContractTests.fs`, 11 shared Theory tests × 2 implementations = 22
      tests. Verified load-bearing: neutered the in-memory fake's `Reorder` to a no-op and confirmed
      only the `"in-memory fake"` case of `Reorder persists the new order` failed, `"real adapter"`
      stayed green — proving the suite catches fake-specific drift, not just real-adapter bugs.
- [x] **10.2** `TemplateApi` contract suite: real record **and** every fake. **The suite pins
      behaviour, not shape:** every refusal in `requirements.md` is asserted with its message, not
      merely that a `Result` came back. Change #1's fake passed its suite while missing two whole
      validations and quoting the wrong spelling in a third message — a suite constrains a fake only
      on the axes it actually asserts.
      `Contracts/TemplateApiContractTests.fs`, 13 shared Theory tests × 2 implementations = 26 tests.
      **Deliberate deviation from change #1's precedent, disclosed:** `SupplierApiContractTests`'s
      fake hand-rolls an independent copy of every validation rule (justified there - only three
      rules). Templates' validation surface is a dozen-plus rules (offset range, capture groups,
      date formats, DateFromField soundness, duplicate/required fields); hand-duplicating all of
      them here would risk the fake itself drifting from the real rules, which is exactly the bug
      class this suite exists to catch. Instead the fake reuses the real
      `ValidateTemplateWorkflow.validateTemplate` and the real CRUD workflows
      (`AddTemplateWorkflow`, `EditTemplateWorkflow`, ...) bound over in-memory dependencies -
      those rules are already exhaustively unit-tested case by case in
      `ValidateTemplateWorkflowTests.fs`. What this suite is uniquely positioned to catch instead,
      and does: drift in the API/workflow composition and in storage-substituted CRUD behaviour
      (not-found handling, replace-not-merge on edit, reorder completeness). `TestTemplate` never
      touches storage, so the fake reuses `TemplateApiFactory`'s own glue
      (`toTestMessage`/`toFieldTestResult`/`splitPastedTextIntoLines`, un-privatized for this) rather
      than duplicating it - there is no real-vs-fake split to test for a pure function.
- [x] **10.3** **Exhaustiveness guards.** A reflection-driven test over `FieldRule`, `TargetField`,
      `ParseHint` and `DocumentPart` that fails if any case lacks a mapper entry, a UI editor and a
      fixture. *Adding a rule kind should break this test until it is finished — that is its job.*
      `Contracts/TemplateExhaustivenessTests.fs`, 27 tests (7 FieldRule + 5 TargetField-as-field +
      5 TargetField-as-DateFromField-source + 3 ParseHint + 3 DocumentPart + 4 DocumentFormat cases).
      Closes a real gap the F# compiler cannot: `toFieldRule`/`toTargetField`/`toParseHint`/
      `toDocumentPart` end in a catch-all (`| unknown -> Error "... has no domain equivalent."`),
      which makes the match exhaustive to the compiler even when a case is missing from it - no
      compile warning would ever catch a forgotten case. Verified load-bearing: temporarily removed
      the `"AttachmentName"` arm from `toFieldRule` and confirmed exactly the `AttachmentName` case
      of `every FieldRule case name is recognised by the rule-kind mapper` failed (26/27 passed, 1
      failed) - the catch-all silently absorbed the missing case exactly as predicted, and the
      reflection probe caught it.
      **UI-editor and fixture coverage are asserted by inspection, not by this reflection test**:
      `TemplatesComponents.fs`'s `ruleKinds`/`hintKinds`/`documentParts`/`documentFormats`/
      `targetFields` lists were checked by hand against the same union case lists and are complete;
      `Tests/Fixtures/MeasuredTemplates.fs` (PR 3) covers `AfterLabel`, `LinesAfterLabel`,
      `RegexCapture`, `SubjectCapture`, `AttachmentName`, `DateFromField` across its four document
      shapes - `FixedValue` has no fixture (it never depends on document content, so a fixture adds
      nothing) and is covered by unit tests instead.
- [x] **10.4** Persisted-shape test: assert `InvoiceTemplates` and `TemplateFieldRules` column names
      by reading the table schema.
      `Contracts/TemplatePersistedShapeTests.fs`, 9 tests: column names for both tables (read via
      `PRAGMA table_info`), a `TemplateName` at maximum length, an awkward non-ASCII name, every
      `DocumentPart` case, every `FieldRule` kind with its payload, every `ParseHint` kind with its
      payload - all round-tripped through the real store, not merely constructed.

## Phase 11 — End to end (required)

- [x] **11.1** `E2E/TemplatesFlowTests.fs`: add → the template appears; edit → new values shown;
      reorder → the new order shown and used; delete → gone; a validation failure → `MudAlert` with
      the **specific** reason and **nothing logged**; a store failure → `MudAlert` and **exactly one
      entry logged**; success after failure clears the alert.
      **Render the dialog, do not stub it.** Drive it through `MudDialogProvider` +
      `IDialogService.ShowAsync`, per `E2E/SuppliersFlowTests.renderEditor` — a `MudDialog` rendered
      standalone emits no markup, so a stubbed callback makes an empty assertion look like a passing
      one. That gap hid two user-visible bugs for the whole of change #1.
      `E2E/TemplatesTestHarness.fs` (real composition root over a temp SQLite file, plus an
      unreachable-store variant, mirroring `SuppliersTestHarness.fs`) + `E2E/TemplatesFlowTests.fs`,
      10 tests, all through `renderBrowser`/`renderEditor` against real rendered markup. The reorder
      test asserts both the rendered order (`IndexOf` comparison in `rendered.Markup`) and that the
      store itself reflects the new order via `GetTemplatesForSupplier`, not only the render. Found
      and fixed a real bug while writing this: `TemplatesComponents.fs`'s rule-kind dropdown was
      labelled `"Rule ruleKindOption"` - fallout from an earlier word-boundary rename regex that
      also matched inside a string literal, not just identifiers - fixed to `"Rule kind"`.
- [x] **11.1a** *(E2E)* The two rendering assertions 9.3 exists to satisfy: **every rule editor
      renders its own label**, and **deleting one of two identical rules removes exactly one**.
      Confirm each fails against the defective form before fixing it.
      Verified both, independently, by temporarily reintroducing each defect and confirming the test
      catches it, then restoring the fix: (1) commented out the `MudText'' { label }` line entirely
      - both `every rule editor renders its own label` and `deleting one of two identical rules...`
      failed. (2) Separately, swapped `List.removeAt index` for `List.filter (fun r -> r <> rule)`
      (delete-by-value) - `deleting one of two identical rules...` failed with 0 occurrences
      remaining instead of 1, confirming it specifically catches the delete-by-value regression, not
      only the missing-label one. (Also tried reproducing change #1's original `MudListItem`
      `Text`-property-vs-child-content trap by setting both `Text` and child content together; this
      MudBlazor version renders both rather than suppressing one, so that specific mechanism no
      longer reproduces here - the label-omission and delete-by-value probes above are the
      regressions this dialog can actually still suffer, and both are now proven caught.)
- [x] **11.2** *(E2E)* A test-panel run: paste text containing a non-breaking space between label and
      value, confirm the panel shows the **normalized** text and that the rule extracts the value.
      `the test panel shows normalized text and extracts a value across a non-breaking space`: sample
      text with a literal U+00A0 between `"Invoice:"` and `"INV-42"`, entered via
      `rendered.Find("textarea").Input`, "Run test" clicked, asserts the extracted reference field
      shows `"INV-42"` - proving `TextNormalization.normalize`'s space-folding is genuinely wired
      into the rendered test panel end to end, not only unit-tested in isolation.
- [x] **11.3** Confirm no test reaches `Startup.Startup`.
      Grepped the whole test project for `Startup.Startup` and for any `Startup.<value>` binding
      that isn't under the `ActionNames.MyDogsbody.Startup.*` data module (a different, unrelated
      thing nested in the same namespace). Only two files `open MyDogsbody.Startup` at all -
      `BlazorTestHarness.fs` and `TemplatesTestHarness.fs` - and both use it solely for
      `*ApiFactory.create*Api`, never `Startup.Startup`'s module-level `let` bindings.

## Phase 12 — Gate (required)

- [x] **12.0** **Take the baseline before writing anything.** Run the build and the full suite on
      `main` as cut, and record the per-level totals. `CLAUDE-project.md` currently claims *399 — 162
      Unit, 74 Integration, 146 Contract, 17 E2E*, which was measured on `split/5-suppliers-ui`
      before PR #8 merged; nobody has run the suite on the merged `main`. A before/after count against
      an unverified before is not a measurement.
      Measured via `git worktree add` at `main`'s real tip (`a189efc`) rather than trusted:
      **400 tests — 162 Unit, 75 Integration, 146 Contract, 17 E2E**, zero skips. One more
      Integration test than the previously-claimed 399, confirming the suspicion this task raised.
      Recorded in `CLAUDE-project.md` and `outcome.md`.
- [x] **12.1** `dotnet build MyDogsbody.sln` — zero errors. **Then also
      `dotnet build MyDogsbody\MyDogsbody.csproj`**: `NU1605` is a warning on the solution build and a
      hard error on the WPF host, so the solution build alone does not prove the app compiles.
      Both clean, `--no-incremental` on the solution build. 0 errors, 2 pre-existing warnings neither
      touched by this change.
- [x] **12.2** `dotnet test` — zero failures, **zero skips**, all four levels. Record totals per level.
      **709 tests — 339 Unit, 109 Integration, 234 Contract, 27 E2E**, zero failures, zero skips.
      +309 over the verified 400-test baseline. Full breakdown in `outcome.md`.
- [x] **12.3** `Contracts/DomainIsolationTests.fs` and `AssertDomainReferencesNothing` still pass.
      3/3 tests pass; the `.fsproj` build target still passes on its own.
- [ ] **12.4** **Acceptance test for ask #6:** run the app, add a supplier, add a template for it,
      run it against pasted text in the test panel, and get the fields out — **with no rebuild, no
      F# change and no restart.** That is the whole point of this change.
      **Not yet performed — needs a human at the running WPF app.** Steps recorded in `outcome.md`
      under *Manual verification (12.4)*. Everything else in this file is done; this is what remains
      before merge.
- [x] **12.5** Confirm `MainWindow.xaml.cs` is untouched.
      `git diff --stat main -- MyDogsbody/MainWindow.xaml.cs` against the tip of `change/2-6-ui` is
      empty.

## Phase 13 — Documentation (required)

- [x] **13.1** `CLAUDE-project.md`: the new domain areas in the project-structure table, the two new
      migrations, the *Build state* totals.
      Project-structure table (`InvoiceTemplates/`, `Invoices/`, `TemplateApiMappers.fs`/
      `TemplateApiFactory.fs`, `TemplateUiType*`/`TemplateApi`, `InvoiceTemplateRecord`/
      `TemplateFieldRuleRecord`), the Status table, the two migrations under *Storage → Main
      database*, and *Build state* (709 tests, plus the corrected/verified 400-test `main` baseline)
      all updated.
- [x] **13.2** `outcome.md`: totals per level, which of the four measured fixtures passed first time,
      and **the measured due-date coverage the fixtures actually achieve** — the number friction #19
      says change #7's value depends on.
      3 of 4 named-supplier fixtures passed on first write (Accounting platform needed one fixture
      text fix, not a rule-kind fix). Measured due-date coverage across the four fixtures: **4/4
      (100%)** produce a `DueDate` — three read directly, one (Invoice-management platform) derived
      via `DateFromField`. Explicitly disclosed as proof the *mechanism* works, not a re-measurement
      of `background.md`'s 12% → 39% population statistic.
- [x] **13.3** Open `change/invoice-templates` for review, with this file's checkboxes ticked and
      `outcome.md` on the branch. **Merge only after Phase 12 passed in full.**
      *Point the reviewer at Phase 1 and task 4.3 first: silent normalization failures and the
      12% → 39% rule are where this change is either right or quietly wrong.*
      Split as six stacked PRs per `#3`'s precedent, one per architectural ring — PR 6
      (`change/2-6-ui`, this UI+contracts+E2E+gate+docs slice) is the last in the stack. **Phase 12
      passed in full except 12.4**, which needs a human at the running app before merge — see above.

---

## Optional

- [ ] **O.1** Duplicate-a-template action. Useful once a supplier needs a second template that
      differs by one label — which the water utility measurably does.
- [ ] **O.2** Warn on the page when a `d/M/yy` format is chosen, since a two-digit year is ambiguous.
- [ ] **O.3** Show, per rule editor, a live preview against the current test-panel text. Cheap once
      9.5 exists, and it is what makes authoring a template a five-minute job instead of a guess.
- [ ] **O.4** Template export / import as JSON. The model is already relational, so this is a read
      and a write (Q5.10). Not in the first pass.
- [ ] **O.5** Revisit `SupplierMatcher.SubjectPattern` as a regular expression. Design decision 8
      keeps it a substring; the argument against changing it is that a matcher runs against **every
      message in the mailbox**, which is where a pathological pattern is least affordable.

## Known risks carried into this change

- **Friction #9 — the compiler's guarantee is replaced by a runtime boundary.** `ValidTemplate` has
  one producer and a private constructor; the exhaustiveness tests in 10.3 are what keep it honest.
- **Friction #19 — everything on the calendar depends on `DateFromField`.** Task 4.3 is required, not
  optional, and 13.2 records the coverage it actually achieves.
- **A user regex can hang a scan.** Timeout on construction, `NonBacktracking` where the pattern
  allows, `RuleTimedOut` as a named error, and task 2.3's catastrophic-backtracking test.
- **Normalization failures are silent.** Phase 1 goes first for that reason, and the test panel shows
  normalized text so a template is authored against what the engine sees.
- **Multi-column layouts are not expressible.** Known and accepted; `SameRowAsLabel` over
  `ReadDocumentContent`'s existing `Word` type is the recorded next rule kind, with one measured
  supplier waiting for it.
