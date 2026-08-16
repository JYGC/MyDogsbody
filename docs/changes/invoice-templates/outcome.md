# Outcome — Invoice templates

Change **#2 of 7**. See [`requirements.md`](requirements.md), [`design.md`](design.md),
[`tasks.md`](tasks.md).

## Gate

- `dotnet build MyDogsbody.sln --no-incremental` — **0 errors**, 2 pre-existing warnings (both in
  `MyDogsbody.Tests`, neither touched by this change: `FS0760` in `PdfDocumentReaderTests.fs`,
  `FS0020` in `CredentialDependencyContractTests.fs`).
- `dotnet build MyDogsbody\MyDogsbody.csproj` — **0 errors, 0 warnings**. Checked separately per
  CLAUDE-project.md's own warning that `NU1605` is a hard error here and only a warning on the
  solution build; it did not recur.
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` — **739 tests, 0 failures, 0 skips**, all
  four levels present:

  | Level | Before | After | Added |
  | --- | --- | --- | --- |
  | Unit | 162 | 357 | +195 |
  | Integration | 75 | 114 | +39 |
  | Contract | 146 | 238 | +92 |
  | E2E | 17 | 30 | +13 |
  | **Total** | **400** | **739** | **+339** |

  The "After" column was itself re-measured during PR #14's review-fix round, which is when the
  discrepancy showed up: the figure first recorded here (`709 — 339/109/234/27`) did not match what
  the branch actually produced (`714 — 339/109/236/30`), understating Contract by 2 and E2E by 3.
  Re-measuring the branch, not only `main`, is what this section's own Task 12.0 reasoning asks
  for; the review round then added 25 further tests, giving the 739 above.

  **"Before" is a verified number, not the figure CLAUDE-project.md previously carried.** Task 12.0
  asked for the baseline to be measured directly rather than trusted, because the `399` figure
  (162/74/146/17) was recorded before change #1's own final PR merged and was never re-checked
  against the merged `main`. Measured here via `git worktree add` at `main`'s actual tip
  (`a189efc`, which does include change #1 in full): **400 tests — 162/75/146/17**, one more
  Integration test than previously claimed. CLAUDE-project.md's *Build state* section has been
  corrected to say so.
- `Contracts/DomainIsolationTests.fs` (3 tests) and the `AssertDomainReferencesNothing` build
  target both still pass — `MyDogsbody.Domain` gained `InvoiceTemplates/` and `Invoices/` and still
  has zero `ProjectReference` elements.
- `MyDogsbody/MainWindow.xaml.cs` is untouched across the whole six-PR stack
  (`git diff --stat main -- MyDogsbody/MainWindow.xaml.cs` against the tip of `change/2-6-ui` is
  empty).

## Manual verification (12.4)

**Not yet performed — this is the one gate item that needs a human at the running app, and it is
the acceptance test for the whole change.** Steps, to run before merge:

1. `dotnet run --project MyDogsbody\MyDogsbody.csproj` from the repository root.
2. Add a supplier under `/settings/suppliers`.
3. Open that supplier's `/settings/suppliers/{id}/templates`, add a template with at least one
   rule per required field (Reference, Amount, Currency).
4. In the template editor's test panel, paste sample text matching the rules and click *Run test*.
   Confirm the normalized text and the extracted field values appear — **with no rebuild, no F#
   change, and no restart.**
5. Delete `MyDogsbody.db` (and `Credentials.db`, `Logging.db` if present) from the repository root
   afterward — they are gitignored nowhere in particular, per CLAUDE-project.md's *Commands → Run*.

## The four measured fixtures

`Fixtures/MeasuredTemplates.fs` (PR 3) — **3 of 4 named-supplier fixtures passed on first write**;
**Accounting platform needed one fix**: its source text originally put `"Payment due:"` and the
date on separate lines while its rule used `AfterLabel` (same-line-only) — a mismatch between the
measured document's actual layout and what got typed into the fixture, not a defect in
`AfterLabel` itself. Fixed by correcting the fixture's text to `"Payment due: 11 Sep 2026"` on one
line, matching the shape `AfterLabel` was chosen to represent.

**Measured due-date coverage across the four fixtures: 4 of 4 (100%) produce a `DueDate`.** Three
read it directly off the document (`Water utility`, `Accounting platform`, `Energy retailer`); one
— **Invoice-management platform** — derives it via `DateFromField IssueDate` plus the supplier's
`PaymentTermDays`, from a document that never states a due date at all. That is the specific
mechanism the **12% → 39%** figure (friction #19, `background.md`) depends on, and this change
proves the mechanism itself works end to end: parse the issue date, read the term off the
supplier, add them, store the result as the derived field.

**This is not a re-measurement of the 12% → 39% population statistic**, and outcome.md is not
claiming it is. `background.md`'s number came from the real mailbox sample; these four fixtures are
synthetic documents in the *measured layouts* with invented values (Q5's fixture-hygiene rule — no
real amount, reference, account number, address or supplier name in version control), deliberately
selected because they were the layouts that proved consistent, not a random re-sample. What 100%
here shows is that the rule the statistic depends on is implemented correctly; the 12% → 39%
figure itself stands as `background.md` already measured it, unchanged and unverified by this
change.

## What building this turned up

1. **Two F# `private` scoping rules that behave differently by kind, confirmed empirically before
   relying on either.** `private` on a *type's* record constructor is assembly-scoped — a different
   project referencing this one cannot construct the type even in the "same" namespace. `let
   private` on a *value* is file-scoped — a different file in the *same* assembly cannot see it
   either. The first meant `MyDogsbody.Database.TemplateRecordMappers` could not construct
   `ValidTemplate` directly despite referencing `MyDogsbody.Domain`; fixed by adding
   `ValidateTemplateWorkflow.reconstructValidTemplate`, the one sanctioned reconstruction path,
   rather than loosening the type's privacy. The second meant `TemplateApiFactory.fs` could not call
   `TemplateApiMappers.toTargetFieldUiString` even from the same project; fixed by removing
   `private` from that one function.
2. **`Regex(..., RegexOptions.NonBacktracking, ...)` throws `NotSupportedException`, not
   `ArgumentException`, when a pattern uses a construct the engine does not support** (lookaround,
   backreferences). Verified with a throwaway build probe before writing `compilePattern`'s catch
   clauses — a plausible-looking version that caught `ArgumentException` instead would have let that
   exception escape the workflow uncaught.
3. **`routeCif "/settings/suppliers/%s/templates"` is Fun.Blazor's parameterised-route form**;
   `routeCi` alone has no parameter slot. Confirmed by a full build succeeding against the real
   package rather than assumed from documentation.
4. **A word-boundary rename regex (`kind` → `ruleKindOption`, used to avoid a Fun.Blazor
   custom-operation name collision) also matched inside a string literal, not just identifiers**,
   turning the rule-kind dropdown's label into `"Rule ruleKindOption"`. Found while writing the E2E
   suite and fixed to `"Rule kind"`. A second occurrence, inside a comment, had already been caught
   and fixed earlier in the same session.
5. **This MudBlazor version does not reproduce change #1's original `MudListItem` Text-vs-child-content
   trap** (setting both a `Text` property and child content; change #1's dialog once rendered one and
   silently dropped the other). Tried reproducing it directly on this dialog — MudBlazor rendered
   both. The label-omission and delete-by-value regressions (see Phase 11 of `tasks.md`, 11.1a) are
   the ones this dialog can still suffer, and both are proven caught by temporarily reintroducing
   each and confirming the E2E suite fails, then restoring the fix.
6. **Two of my own test assertions were weak enough to pass against deliberately broken production
   code**, caught by the "prove the test actually catches the defect" pass CLAUDE.md's testing
   mandate implies for anything atomicity-sensitive: `TemplateStoreTests`'s `updateOne` atomicity
   test used identical rule text for its "before" and "attempted update" states, so a partial commit
   was indistinguishable from a clean one; and its `reorder` atomicity test relied on a sorted-list
   comparison that a tie-break in a stable sort happened to satisfy even with a broken transaction.
   Both fixed by giving the test genuinely distinguishable before/after states and asserting a
   specific value rather than a whole-list comparison.

## Design deviations

- **`ApplyTemplateWorkflow` and `SelectTemplateWorkflow` both carry a `TemplateId`/`SupplierId`
  parameter design.md's signatures did not show.** `ApplyTemplateWorkflow.applyTemplate` needs a
  `TemplateId` to stamp onto the `ExtractedInvoice` it returns (a later change's ledger row needs to
  know which template produced it); `SelectTemplateWorkflow.selectTemplate` needs the `SupplierId`
  it was already given by its caller to pass through to `applyTemplate`. Both are additive, not
  contradicting anything design.md specified.
- **`TemplateError` gained three cases beyond design.md's original listing** —
  `TemplateSupplierIdInvalid`, `TemplateIdInvalid` (an untrusted id string arriving at a workflow
  boundary, the same shape `SupplierIdInvalid` already covers for Suppliers) and
  `DerivationSourceIsSelf` (a `DateFromField` rule naming its own field as its source — generalised
  beyond design.md's DueDate-only wording, since the same circularity is possible for any field).
- **`LoadSuppliersForTemplates` is its own dependency function type**, not a reuse of
  `Suppliers.LoadSuppliers` — a decision made explicitly before this PR's work began (see
  `background.md`'s reservation table and the requirements/design/tasks edit in commit `a189efc`).
  `Suppliers.LoadSuppliers` returns `Result<_, SupplierError>`; reusing it here would mean either a
  `Result.mapError` at every call site or one dependency type spanning two areas' error unions and
  owing a contract suite in both. The adapter underneath is the same `SupplierStore.getAll`; only
  the error mapping in `TemplateApiFactory` differs.
- **`TemplateApiContractTests`'s fake deliberately does not hand-roll validation the way
  `SupplierApiContractTests`'s does** (see `tasks.md` 10.2). Suppliers' validation surface is three
  rules, small enough to duplicate safely; Templates' is a dozen-plus. The fake instead reuses the
  real `ValidateTemplateWorkflow` and the real CRUD workflows bound over in-memory dependencies, so
  the suite is aimed at what it can uniquely catch — API/workflow composition and storage-substituted
  CRUD behaviour — rather than re-deriving already-unit-tested validation rules by hand.

## Not implemented (Optional, deferred)

None of `tasks.md`'s five optional items were pulled into this change's required scope:

- **O.1** Duplicate-a-template action — useful once a supplier needs a second template differing
  by one label, which the water utility fixture measurably does, but not required to prove the
  engine.
- **O.2** Warn on an ambiguous `d/M/yy` two-digit-year format.
- **O.3** Live per-rule-editor preview against the test-panel text.
- **O.4** Template export/import as JSON.
- **O.5** Revisit `SupplierMatcher.SubjectPattern` as a regex rather than a substring.

`tasks.md`'s *Known risks carried into this change* section names two that this change accepts
rather than closes: multi-column layouts stay unrepresentable (a `SameRowAsLabel` rule kind is the
recorded next step, with one measured supplier already waiting for it), and a user-authored regex
can still hang a scan without the `NonBacktracking`/timeout/`RuleTimedOut` guard this change adds —
that guard is required scope here (task 2.3) and is implemented, not deferred.
