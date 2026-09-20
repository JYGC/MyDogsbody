# Comments to names — outcome

## The answer, in numbers

You asked whether long names could replace the large comment blocks. Measured over `.fs` and `.cs`
(the basis of requirements.md's inventory), comment lines went **6,798 → 5,337 (−1,461, 21%)**, and the
build and all 1,726 tests are unchanged throughout. What that 21% is made of:

| Phase | Ring | Lines removed | How |
| --- | --- | ---: | --- |
| 1 | `Domain` (+ call sites) | 188 | renames, deleted restatements |
| 2 | `Builders`, `Exceptions`, `Exceptions.Types` | 3 | deleted restatements |
| 3 | `Database.Models`, `Database`, `Database.Migrations` | 24 | renames, deleted restatements |
| 4 | `Integrations.*` | 50 | renames, deleted restatements |
| 5 | `Logging` | 2 | deleted restatements |
| 6 | `Startup` | 24 | renames, deleted restatements |
| 7 | `UI.Types` | 6 | deleted restatements |
| 8 | `UI.Portal`, host | 21 | renames, deleted restatements |
| 9 | `Tests` | 165 | deleted Arrange / Act / Assert markers |
| | **Names and deletions** | **483** | **7% of the original comment lines** |
| 10 | all | 978 | relocated to `rationale/`, verbatim, one-line pointer left |
| | **Total** | **1,461** | |

So **a longer name absorbed or replaced about 7% of the comment lines, and another 14% were moved
rather than removed.** What is left, 5,337 lines, is 61% rationale by the classifier (69% of the 2,717
lines outside the tests): history, measured evidence, a requirement or review citation, a constraint
on callers, which a name cannot hold. About a quarter (1,319 lines, 680 of them outside the tests) is
still labelled "describes a declaration". Those are the published names, defined terms and dependency
contracts that phases 1 to 8 kept on purpose, each read and each recorded, plus the ~500 test
comments phase 9 did not reach. The classifier is a vocabulary heuristic: that quarter is a measure of
what remains to be read, not a proof that it cannot become names.

Not done, with the measurement behind each in the phase it belongs to: the banner dividers and the
describing comments in the tests (phase 9). Two things went wrong on the way and were fixed, and are
in the phase sections: a false line-ending claim in design.md (phase 1) and a total quoted on the wrong
file set (phase 2). The suite hit the one documented LiteDB flake twice (phases 6 and 10); both are
recorded with their stack traces, and neither run was reported as green.

## Phase 1 — `MyDogsbody.Domain`

Each later phase adds a section below.

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, exactly the two warnings `origin/main` already has
  (`FS0760` in `PdfDocumentReaderTests.fs`, `FS0020` in `ScanWindowStoreTests.fs`).
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj`: **1726 passed, 0 failed, 0 skipped** —
  the same total as `main`, and every assertion unchanged. All four levels are in that suite. The
  known LiteDB first-use flake did not appear.
- 64 files changed, +679 / −704. Domain 43; `Integrations.Documents` 1; `Integrations.Google` 1;
  `Startup` 3; `Tests` 15; `MeasureScan` 1 (one comment — see *Decisions*).
- The persisted-shape search (design.md) has no match in production code, so no renamed union
  case reaches a database column or a message.
- No new tests: this is a rename with no behavior change (requirements.md → Testing note).
  Branch `change/comments-to-names`, from `origin/main` `011d924`; nothing is committed.

### What moved, honestly

`MyDogsbody.Domain`, by `classify-comment-blocks.awk`:

| | Before | After |
| --- | ---: | ---: |
| Comment lines | 1,416 | 1,217 |
| Blocks that describe a declaration | 120 | 84 |
| Blocks that describe a statement | 16 | 15 |
| Rationale blocks / lines | 167 / 1,087 | 165 / 979 |

That is 199 fewer comment lines in the ring (14%), and 188 fewer across the repository
(6,798 → 6,610, `.fs` and `.cs` together, the basis of requirements.md's inventory). It is a smaller dent than the inventory in requirements.md could suggest, and
the reason is in the last row: **979 of the ring's remaining 1,217 comment lines are rationale**,
which this change does not touch. The lines that moved into names are mostly the *first* sentence
of a longer block; the rest of the block stays.

### Public renames (old → new)

Each was measured before it was made (design.md → What is left alone). Call sites in every ring
were updated in the same change.

| Old | New | Uses |
| --- | --- | ---: |
| `SupplierMatcher.SenderAddress` | `SenderAddressEqualToIgnoringCase` | 12 |
| `SupplierMatcher.SenderDomain` | `SenderDomainEqualToIgnoringCase` | 16 |
| `SupplierMatcher.SubjectPattern` | `SubjectContainsSubstringIgnoringCase` | 9 |
| `TemplateError.TemplateRuleShapeInvalid` | `TemplateRuleShapeStringFromTheUiHasNoDomainEquivalent` | 17 |
| `InvoiceSyncKey.PropertyName` | `PrivateExtendedPropertyNameOnAGoogleCalendarEvent` | 11 |
| `InvoiceSyncKey.parts` | `supplierRowIdAndInvoiceReferenceAsPlainStrings` | 3 |
| `Money.MaxAbsAmount` | `MaximumAbsoluteAmountAsATypoGuardNotAPolicy` | 4 |
| `Word.Bottom` | `BottomEdgeHeightAboveThePageBottom` | 17 (one in a comment) |
| `Word.Left` | `LeftEdgeDistanceFromThePageLeft` | 15 |
| `DocumentFormatUnsupported`'s field label `format` | `fileExtensionLowerCasedWithoutTheDot` | label only; every use is positional |

Two test titles that embedded a renamed name were updated with it
(`toUnvalidatedTemplate rejects … as TemplateRuleShapeStringFromTheUiHasNoDomainEquivalent`, twice)
and so was the `InvoiceSyncKey.parts` test.

About 60 private functions, values, types and union cases were renamed inside the ring, all in the
diff. The ones that show the intent best: `ensureExists` → `ensureAStoredSupplierAlreadyHasTheEditedId`,
`reconcileSelection` →
`clearSelectionIfPreviouslySelectedAccountIsAbsentFromFreshDiscoveryReportingWhetherItDid`,
`isContinuation` → `currentLooksLikeAWrappedContinuationOfPrevious`, `MessageOutcome`'s
`Extracted` / `Recorded` / `Skipped` → `InvoiceToStore` / `ProblemToRecord` /
`NothingBecauseTheKeyIsTombstoned`.

### Structural changes, and the tests that drive them

Requirements rule 6: these are the places the change is more than a rename.

- **`selectCandidates` returns a named record** (`CandidateDocumentsOfWhichThereIsAlwaysAtLeastOne`)
  instead of a `head * tail` tuple, so "never fewer than one" is in the type. Driven by
  `ApplyTemplateWorkflowTests`: *two attachments cannot fill one invoice between them*, *an
  attachment yielding every required field wins over an earlier one yielding none*, *when no
  attachment yields every required field the reported error is the last one tried*, *an AnyPart
  template is held to the same one-attachment-at-a-time rule*, *the message body stays in scope
  for every attachment*.
- **`Money` gets labelled fields** (`amount`, `currencyCode`) in place of a trailing comment. Driven
  by the `Money.create …` tests in `InvoicesTypesTests`.
- **`parseAmount`'s sign branch** went from `Some (if c then -(abs v) else v)` to
  `if c then Some (-(abs v)) else Some v`. Driven by *AsMoney reads an amount wrapped in
  accounting parentheses as a credit*, *… does not read a bracket that merely sits near the number
  as a credit* and *… still reads a credit note*.
- **Intermediate `let`s** replaced comments in `listSuppliers`, `listTemplates`,
  `listGoogleAccounts`, `listScanWindows`, the three `delete*` / `undelete*` workflows,
  `selectMailAccount`, `selectScanWindow`, `matchSupplier`, `InvoiceSyncKey.derive`,
  `SourceMessageId.createOrDefault`, `addTemplate` and `editTemplate`. Each is driven by that
  workflow's own unit tests.

### What stayed, and why

The classifier still reports 84 declaration blocks and 15 statement blocks in the ring. Each was
read. None is a description a name could carry without losing something:

- **Opaque identifier types** — `SupplierId`, `TemplateId`, `InvoiceId`, `MailAccountId`,
  `SourceMessageId`, `GoogleAccountId`, `CalendarId`, `CalendarEventId`. "Opaque to the domain" is
  a constraint on callers, not a description.
- **Dependency function types**, which CLAUDE-project.md calls published interfaces —
  `AuthoriseAccount`, `DiscardAuthorisation`, `MarkSynced`, `LoadAllLedgerKeys`, `UpdateSupplier`,
  `UpdateTemplate`, `ReadDocumentContent`, `ReadDocumentText`, `GetCurrentTime`. The comment is
  the contract.
- **Types whose doc defines a term** the specs use — `ProfileRootPath`, `GoogleEmail`,
  `AvailableCalendar`, `CalendarEvent`, `InvoiceSyncKey`, `InvoiceIssueDate`, `PaymentTermDays`,
  `NormalizedLine`, `NormalizedPart`, `ScannedMessage`, `ExtractedInvoice`, `ValidTemplate`.
- **Union-case docs that say when a case is raised or whether it is logged** — `EventRejected`,
  `UpdateEvent`, `DeleteEvent`, `LeaveAlone`, `Skipped`, `AlreadyGone`, `InvoiceNotFound`,
  `InvoiceStoreFailed`, `SupplierGone`, `NoAccountSelected`, `ScanWindowInvalid`,
  `CannotDeleteLastScanWindow`, `ProfileRootUnreachable`.
- **"Why" notes with none of the words the classifier looks for** — for example *"The order the
  picker renders them in"*, *"So the table has a stable order"*, *"An empty store is not an
  error"*, *"SupplierName already trims, so only case remains to compare here"*. They are what is
  left of a block once its first sentence moved into a name.
- **Statement-level (15)** — five section dividers inside types files (a nested module would
  change every qualified name in the namespace), and invariant notes such as *"matchSupplier only
  ever returns SupplierNotRecognised here"* and *"Unreachable: validation refuses every derivation
  but DueDate from IssueDate"*.
- **Not renamed because the ripple outran the sentence** — `compilePattern`, `computeCutoff`,
  `UploadableInvoice.ofStored`, `SelectionCleared`, `SupplierGone`, `InvoiceStoreFailed`,
  `InvoiceNotFound` (design.md carries the measurements).

### Decisions the reader may want to overrule

1. **Ubiquitous names were not lengthened** (requirements.md → Rules applied #4). `SupplierId` and
   `PaymentTermDays` keep their names and their docs. Lengthening them is a follow-up with a very
   large ripple.
2. **Stage-type docs were deleted**, not renamed into the type — `UnvalidatedSupplier`, `ValidSupplier`,
   `StoredSupplier`, `StoredTemplate`, `StoredInvoice` and the like. They restated the name and the
   stage table in CLAUDE-project.md.
3. **Docs that narrated a workflow's steps were deleted** — `deleteSupplier`, `deleteTemplate`,
   `selectMailAccount`, `scanForMailAccounts`, `registerGoogleAccount`'s opening line. The code
   shows the steps.
4. **`Word`'s two coordinates were renamed**, the highest-ripple rename made (17 and 15 uses, in six
   files across three rings; PdfPig's own `BoundingBox.Bottom` / `.Left` are untouched). One sentence — "coordinates grow upwards, as PDFs measure them" — was
   worth it to me; it is the first thing to revert if it is not.
5. **`MeasureScan/Program.fs` was touched**, one comment naming
   `MatchSupplierWorkflow.senderDomain`, which no longer exists under that name. The project is
   out of scope and outside the solution; the alternative was a comment that points at nothing.
6. **A clause was dropped rather than carried into a name.** `validateMatchers`'s doc said it
   stops at the first failure "and naming which rule failed", but the code passes on only the
   reason `SupplierMatcher.create` returned. The name says what the code does.

Stale comments noticed and left alone: `PaymentTermDays` says "Nothing in this change reads it -
DateFromField in change #2 is what it exists for"; `MatcherKind` says "the same reason
Infrastructure exists in the credentials area today", and `Infrastructure` was removed by
`credentials-per-provider`. Both are history, not descriptions, and fixing them is a separate
change.

### Deviations from the plan

- design.md first said every `.fs` file is CRLF. That was wrong: at `011d924` the working tree
  holds 141 CRLF and 163 LF files. The patcher preserves each file's own style, so no file
  changed style; design.md is corrected.
- The sixth form in my first sketch — a named active pattern in place of a comment above a match
  arm — was not used. The two candidates in `ScanForInvoicesWorkflow` are rationale.

### Next

The Tests phase is where most of the mechanical volume is: 219 Arrange/Act/Assert markers, 187
banner dividers and about 250 descriptive comments.

## Phase 2 — `MyDogsbody.Builders`, `MyDogsbody.Exceptions`, `MyDogsbody.Exceptions.Types`

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings `origin/main` already has.
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj`: **1726 passed, 0 failed, 0 skipped** —
  the same total as `main` and as phase 1.
- 1 file changed, +2 / −5: `MyDogsbody.Exceptions.Types/ActionNames.fs`. Nothing was renamed, so
  no call site changed and the persisted-shape search has nothing new to look at. No new tests,
  for the same reason as phase 1.
- Classifier over the ring: 5 blocks / 27 lines → 5 blocks / 24 lines, every block rationale or a
  contract. Repository-wide, `.fs` and `.cs` together: 6,610 → 6,607.
- Recounted on `origin/main` while writing this: the F# files alone are 2,132 blocks / 6,730 lines
  and now 2,093 / 6,539. The 68-line difference from the 6,798 in requirements.md is the eleven C#
  files (19 blocks), which the inventory counted and an `.fs`-only run does not. No figure above
  was wrong; requirements.md now says which files it covers.

### Why the ring has so little in it

- `Builders` (`HandleErrorBuilder.fs`, 59 lines) and `Exceptions` (`ExceptionHelpers.fs`, 6 lines)
  hold no comments. Their members are the names an F# computation expression requires (`Bind`,
  `Return`, `TryWith`, …), and their parameters and locals are already full words
  (`caughtApplicationException`, `translatedException`, `deferredComputation`).
- All five blocks are in `Exceptions.Types`.

### What changed

Two openers in `ActionNames.fs` were deleted because they restated the module's name:

- `Startup` — "The composition root's own actions."
- `Database` — "The main SQLite database's own actions." The sentence after it already says
  `MyDogsbody.Database` is the application's main store, so the word "SQLite" left with it; it is in
  CLAUDE-project.md and nothing in this module depends on it.

### What stayed, and why

- **`ActionNames`'s module doc (10 lines), `Startup` (2), `Database` (2), `Thunderbird` (6).** Why an
  entry names what it names, why `Database` sits beside `Integrations` and not in it, why four
  Thunderbird files have no entries, and the history behind `ActionNamesTests`. Rationale. The first
  sentence of the module doc defines "action", the term the rest of the module is written in.
- **The module names cannot absorb any of it.** The nested modules mirror the real code path — that
  is the module's own convention — and `ActionNamesTests` requires every string to end with the name
  of the binding that declares it. `ActionNames` is at 262 sites in 50 files.
- **`MyDogsbodyException`'s two-argument constructor (4 lines).** The contract of a published
  constructor, with 19 call sites in 8 files. A constructor cannot be renamed, and a factory in its
  place would move every one of them.

### Noticed and left alone

That constructor's doc says the failure is one where "the user simply typed something the domain
rejected". All ten of its production call sites (`Startup/*ApiMappers.fs`) translate
`AuthorisationFailed`, `CalendarUnreachable`, `CalendarRateLimited`, `EventRejected` and the
`*StoreFailed` cases — failures the domain already carries as a message string. The same mappers
build a user-typed rejection with the *three*-argument form wrapping an `ApplicationException`
(their local `expected` helper), so the comment describes the opposite of how the constructor is
used. "No stack trace worth reading" is true of the ten; the rest of the sentence is not. Fixing it
means deciding what the constructor is for, which is a change of its own.

### Decisions the reader may want to overrule

- **Phase 2 is a three-line change.** With this little in the ring, folding it into phase 3's PR
  would leave the stack one PR shorter. It is kept as its own phase here because that is what
  tasks.md said; the choice is yours.

## Phase 3 — `MyDogsbody.Database.Models`, `MyDogsbody.Database`, `MyDogsbody.Database.Migrations`

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings `origin/main` already has.
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj`: **1726 passed, 0 failed, 0 skipped.**
- 8 files changed, +80 / −90, all in `MyDogsbody.Database` (7) and `MigrationSetup.fs` (1).
  `Models.fs` and every file under `Migrations/` are untouched. No SQL string, table name, column
  name or record field changed — the diff lines that mention a table or column are all comments.
- Classifier over the ring's `.fs` files: 91 blocks / 402 lines → 84 / 378 (−24, 6%). Rationale
  51 / 324 → 48 / 309; describes-a-declaration 24 / 54 → 22 / 49; describes-a-statement 8 / 16 →
  6 / 12. Repository-wide, `.fs` and `.cs`: 6,607 → 6,583.

### Why this ring gives so little

- **`Models.fs`: a record field name *is* a column name.** Dapper maps by name and the migrations
  are the schema's only source of truth, so `AttachmentFormat`, `RuleText`, `SelectedScanWindowDays`
  and the rest cannot take a longer name. Their doc comments say how each column is encoded (TEXT
  format, when it is NULL) — the persisted-shape contract, not a description of a name.
- **The 12 migration blocks (136 lines) are 36% of the ring's comment lines and stay.** CLAUDE-
  project.md: "Never edit a migration that has been applied". A comment-only edit would not change
  the schema, but the rule is a guardrail and every one of these has shipped. Eleven blocks are
  rationale; one 2-line block in `…0009` describes a statement.
- **Store function names are pinned.** `getAll`, `insertOne`, `markSynced` and the rest are the
  strings in `ActionNames`, which `ActionNamesTests` requires to end with the declaring binding's
  name. Their docs say what `Ok None` or `true` means, or why a query has no `LIMIT`.

### What changed

- **Direction prefixes deleted** (10): "Domain -> persistence." / "Persistence -> domain." above
  `to…` / `from…` / `encode…` / `decode…` functions, which say it in their names. Where a "why"
  followed in the same doc it stays: *"Returns Result because the column is a plain TEXT value…"*.
  The two `toRowId` docs lost their opening "The identifier the domain carries, as the store's own
  key type", which the signature (`SupplierId -> int`) already says.
- **A first sentence that restated an 8-line body was deleted** from both `inTransaction` docs
  ("Runs `work` with the connection open and inside a transaction, committing on success"), and
  from `rollbackAll`'s doc; the rollback rationale that follows each stays. `InvoiceCalendarEvent
  Store`'s module doc lost the list of its own functions; a comment on `timestamp` / `dateOnly`
  that restated the two format strings in their bodies was deleted.
- **Private renames.** `buildRunner` → `buildRunnerOverEveryMigrationInThisAssemblyReturningThe
  ServiceProviderThatOwnsIt` (3 uses); `unitSeparator` → `unitSeparatorBetweenDetailFields` (8);
  `ForeignKeyViolation` → `ExtendedResultCodeSqliteReportsForAForeignKeyViolation` (2); locals
  `matchersBySupplierId` → `everyMatcherFromOneQueryGroupedBySupplierId`, `ruleRowsByTemplateId` →
  `fieldRulesOfTheseTemplatesFromOneQueryForEveryFieldRuleGroupedByTemplateId` (it filters after
  the one query, so the name says so), and `updatedRecord` in both stores.
- **A comment became a named `let`** in five places: the two `insertOne`s and two `updateOne`s
  ("every field is already known … no need to read back what was just written") and
  `toNewInvoiceRecord`'s trailing `// set by the store at write time`
  (`scannedAtPlaceholderBecauseTheStoreSetsItAtWriteTime`). Each is exercised by that store's or
  mapper's own tests; the value of the placeholder is still `""`.
- **One public parameter renamed:** `MigrationSetup.rollbackToVersion`'s `version` →
  `lastMigrationVersionThatStaysApplied`. Its three callers pass it positionally. The claim is
  checked: `rollbackToVersion … 20260810000008L` in `InvoiceCalendarEventsMigrationTests` rolls
  back migration 9 only.

### Repaired

`InvoiceRecordMappers.fs:100` read `EXHAunitSeparatorTIVE` on `origin/main`: an earlier rename of
`US` reached inside the word *EXHAUSTIVE*. It is now "Exhaustive". It was the only such word in the
repository (grepped for a lower-case identifier sitting inside a word).

### What stayed, and why

- **`inTransaction` was not renamed** although its first sentence described it: CLAUDE-project.md
  and two earlier change folders (`invoice-templates`, `sqlite-pool-flake`) cite it by name.
- **`DatabaseContextSetup`'s 18-line comment** on `Foreign Keys=True` and `Pooling=False` is one
  measured rationale (0.090 vs 0.470 ms per open/query/close). A binding named for the connection
  string would restate the literal it holds.
- **`isMissingSupplier`'s 22-line doc** is history and a spec citation; its first sentence is close
  to the name but the name is used from `Startup` and the tests.
- **Store docs that give the meaning of a return value** (`Ok None`, `true`/`false`) and the
  cascade notes ("removed by the database's own cascade (migration …0002)").
- The eight `// ---------- section ----------` banners in `InvoiceStore` and `InvoiceRecordMappers`
  — a nested module would change the qualified names the tests and `ActionNames` use.

### Noticed and left alone

`InvoiceStore.upsertInvoice` defines a local `toObj` with the same body as the module-level private
`toObj` further down the file. Not a comment; a separate change.

### Decisions the reader may want to overrule

- The migrations were left alone as a class. If you would rather trim the ones whose comments
  describe rather than explain, that is a change with its own review, since each edit is to a file
  FluentMigrator has already applied.

## Phase 4 — `MyDogsbody.Integrations.Documents`, `.Google`, `.Thunderbird` and their `*.Database.Models`

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings `origin/main` already has.
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj`: **1726 passed, 0 failed, 0 skipped.**
- 14 files changed, +119 / −141: Documents 5, Google 7, Thunderbird 2. **No `.cs` file changed.**
- Classifier over the three projects' `.fs` files: 188 blocks / 773 lines → 171 / 723 (−50, 6.5%).
  Describes-a-declaration 70 / 157 → 53 / 128; rationale 99 / 578 → 99 / 557; statements and
  banners unchanged. Repository-wide, `.fs` and `.cs`: 6,583 → 6,533.

### What is off limits in this ring

- **The `.cs` entities** (`GoogleCredential`, `GoogleAccountEntity`, `ScanWatermarkEntity`, …; 45
  comment lines). LiteDB stores a C# property name as the document's field name and is
  schemaless, so a rename orphans stored data silently (CLAUDE-project.md → *LiteDB entity
  shape*). Their comments are the persisted-shape notes — `ScanWatermarkEntity`'s explains why a
  `DateTime` is stored as ticks.
- **Published names.** `authorise`, `reauthorise`, `loadCredential`, `fetchRealAccountEmail`,
  `listCalendars` and the other public adapter functions are bound in `Startup` and named in the
  contract suites and CLAUDE-project.md; the three `…Prefix` / `…Message` literals are matched by
  string in `GoogleAccountApiMappers`.

### What changed

- **Private renames, each carrying the sentence it replaced.** Documents: `lineTolerance` →
  `baselineDistanceInPdfUnitsWithinWhichWordsAreOneLine`, `blockGapFactor` →
  `lineGapAsAMultipleOfThePageLinePitchAboveWhichANewBlockStarts`, `pageLines`, `tagBlocks`,
  `blockTags`, `cellTags`, `renumber`, `toBlocks`. Google: `consentTimeout`,
  `clientSecretRowId`, `listAllPages`, `listAllEventPages`, `toInvoiceSyncKey`. Thunderbird:
  `fromLineBytes` → `asciiBytesOfFromFollowedByASpace` (the array literal is opaque; the comment
  was decoding it), `headerBlockAndRest`. Every one has 1–4 uses, all in its own file.
- **Sentences that restated a name or signature were deleted**, keeping the "why" that followed:
  the eight "entry point — the default `HttpClientFactory`" / "the seam `xVia` closes over"
  one-liners in `GoogleCalendarClient` (the pattern is documented once, on
  `listCalendarsVia`), the three stage-type docs in `GoogleCredentialTypes` (same call as phase 1's
  `UnvalidatedSupplier` and friends), the direction prefixes and "whole row" in the two Google
  entity mappers, and the openers of `toMailAttachment`, `parseMessage`, `tryParseMessage`,
  `classifySegment`, `accountWithReadableStore`, `SegmentOutcome`, `enumerate`, `dispatch`,
  `readText`, `grantsCalendarAccess`, `getDatabaseContext` and `FolderWatermark`.
- Four over-long call sites the renames produced were wrapped; one `if … .IsSome` became
  `|> Option.isSome` (same behaviour, and the streaming count is covered by the mbox tests).

### Repaired

`MailFolderReader.headerBlockAndRest`'s doc said it returns "the index just past the blank line".
It returns `(headerBlock, rest)`. That was wrong on `origin/main`; the doc is now the rename plus the
one sentence that was true (no blank line at all in the last message means it is torn).

### What stayed, and why

- **The incident notes** — `MailFolderReader`'s 18-line `NullReferenceException` account, the
  `GetAwaiter().GetResult()` note in `authoriseWith`, the PATCH-not-PUT note in `updateEventVia`.
  They are measured history a name cannot hold; CLAUDE-project.md repeats one of them, the
  `GetAwaiter().GetResult()` rule.
- **Module docs** that state which dependency type each adapter satisfies (contract), and the
  outer-ring shape paragraphs that each store repeats.
- **The eight `// ---------- section ----------` banners** in `ThunderbirdStore` and
  `ThunderbirdEntityMappers`, for the reason given in phase 3.

### Noticed and left alone

`GoogleCalendarClient`'s module doc narrates changes #6 and #7 and still calls
`Startup/GoogleAccountApiMappers.fs` "a later phase's" mapper, though it exists. It is history, not a
description, so rewriting it belongs to a change that owns that file.

## Phase 5 — `MyDogsbody.Logging`, `MyDogsbody.Logging.Database.Models`

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings. `dotnet test`: **1726 passed,
  0 failed, 0 skipped.**
- 2 files changed, +2 / −4. Classifier: 6 blocks / 27 lines → 6 / 25. Repository-wide: 6,533 → 6,531.

### Why so little

The ring is 158 lines of code with six comment blocks, and five of them say why: the first-use race
that the warm-up narrows, why there is no severity field, why one type serves the whole component,
why `addException` returns a `Result` that `Startup` discards. `ExceptionLog.cs` has no comments.

### What changed

Two openers that restated a name were deleted: "One exception, as the log records it" above
`ExceptionLogEntry`, and "The log store's handles" above `LoggingDatabaseContext` (the sentence after
it already says what the record is and which tier it belongs to).

### What stayed

`ExceptionRepository.insertOne`'s "The Exceptions collection of the log database. Errors only - each
log type gets its own collection" and `ExceptionUseCases.addException`'s "What the composition root
partially applies into handleError's writeLog": both say where a function sits in the composition,
which its name cannot.

## Phase 6 — `MyDogsbody.Startup`

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings.
- `dotnet test`, **three observations, all reported:**
  1. Full suite, first run: **1725 passed, 1 failed, 0 skipped.** The failure was
     `ThunderbirdDependencyContractTests.a saved selection is visible to a later load, and clearing
     it persists as absent (implementation: "real adapter")`, with
     `InvalidOperationException: Collection was modified; enumeration operation may not execute` out
     of `BsonMapper.SerializeObject`, raised at the warm-up line in
     `ThunderbirdDatabaseContextModule.getDatabaseContext`. That is the one known flake
     CLAUDE-project.md records (*Per-integration databases*), captured stack and all; nothing in this
     phase touches LiteDB, and phases 2–5 ran the same suite clean.
  2. That test class alone, three runs: 38 / 38 passed each time.
  3. Full suite, second run: **1726 passed, 0 failed, 0 skipped.**

  Per CLAUDE-project.md the flake is named here rather than the suite being called green on the
  strength of a re-run; run 1 is the run that counts as observed, and it is not a regression.
- 10 files changed, +61 / −67, all in `MyDogsbody.Startup`.
- Classifier: 610 → 586 lines (−24; 124 blocks, from 135). Describes-a-declaration 60 / 136 →
  47 / 116. Against `origin/main` the ring went 609 → 586: phase 1 had added one line here when it
  wrapped a renamed call. Repository-wide, `.fs` and `.cs`: 6,531 → 6,507.

### What changed

- **Docs that restated the binding beneath them were deleted:** `ListCalendarEvents` /
  `CreateCalendarEvent` / `UpdateCalendarEvent` / `DeleteCalendarEvent` "as the composition root binds
  it" above `bind…Event`; the stored-client-secret line above `bindLoadClientSecret`;
  `RegisterAccount` / `ReauthoriseAccount` "over whichever consent flow it is handed" above
  `…With`; `getCalendarsForWith`'s opener; `createInvoiceSyncApi`'s "the composition root's entry
  point" (the `…With` doc already says which is which); `supplierNames`; two `// Outbound: …`
  comments above `let toException = …toMyDogsbodyException`; the openers of `Startup.fs`,
  `causeSentence`, `toMatcherKind` and `toMatcherKindUiString`.
- **Private renames:** `resolvedWindow` → `rememberedScanWindowOrTheFallbackIfItNoLongerExists`;
  `toUnvalidatedMatchers` → `toUnvalidatedMatchersStoppingAtTheFirstUnrecognisedKind` (2 uses);
  `toUnvalidatedRules` → `toUnvalidatedRulesStoppingAtTheFirstUnrecognisedShape`, which made a "the
  same way the other one does" comment on each redundant; `toFailureReasonFor` →
  `theReasonThisFieldsRowCarriesOutOfTheSingleErrorAWholeRunHandsBack`.

### Repaired

Two doc blocks were merged above the wrong function on `origin/main`, so each function's hover
text was another function's doc:

- `TemplateApiMappers`: the doc for `toFieldFailureReason` (the sentence the test panel prints, why
  `string error` was replaced) sat on top of `toFailingField`'s, and `toFieldFailureReason` — public,
  used by the factory — had none.
- `TemplateApiFactory`: the doc for `toFailureReasonFor` ("the reason THIS field's row carries…") sat
  on top of `ruleFor`'s.

Each block was **moved verbatim** onto the function it describes; the only wording removed is the
opening sentence that became the `toFailureReasonFor` rename.

### What stayed, and why

- **The six `*ApiFactory` module docs** ("Where the abstract meets the real: …") and the six
  `*ApiMappers` ones. They repeat, near enough word for word, what CLAUDE-project.md → *Composition
  root* says once. They are the natural first target for task 10, not a name's job: the modules are
  named in the tests, the host and CLAUDE-project.md.
- **`Inbound:` / `Outbound:` docs on the `to…Error` and `toMyDogsbodyException` functions.** "Becomes
  the one domain case that stands for infrastructure failure" says something the name does not: it is
  *one* case.
- **The measured accounts** — `TemplateApiFactory.TestTemplate`'s 17-line note on the payment term,
  `toFieldRule`'s on `FixedValue null` and the log entry it wrote, `toParseHint`'s
  `NullReferenceException` — and `InvoiceSyncApiFactory`'s PR-review notes. History a name cannot
  hold.
- **The twelve banners**, for the reason given in phase 3.

## Phase 7 — `MyDogsbody.UI.Types`

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings. `dotnet test`: **1726 passed,
  0 failed, 0 skipped.**
- 4 files changed, +6 / −12. Classifier: 194 → 188 lines (71 → 65 blocks). Repository-wide, `.fs`
  and `.cs`: 6,507 → 6,501.

### Why so little

Every name in this project is a published one: a record field is read by `UI.Portal`, written by
`Startup`'s mappers and asserted in the tests, and an API record's member names are the interface the
UI is allowed to reach. Nothing could be renamed. What is left to trim is the doc that restates the
member beside it.

### What changed

Deleted because they repeated the name: "The persisted problems, for the problems view" and "The
tombstones, for the tombstones view" (`InvoiceApi`); "What kind of action a plan row or outcome row
is" (`SyncPlanActionUiType`); "The result of one scan, as the page shows it above the table"
(`ScanResultUiType`); "Whether the client secret field is currently open for editing"
(`IsEditingClientSecretAval`); "Closes the editable field without saving, leaving the stored value
untouched" (`CancelEditingClientSecret`). Three docs lost an opening clause that repeated their type
or field name and kept the rest (`InvoiceUiType`, `ScanWindowUiType`, `CalendarsByAccountIdAval`).

### Tried and rejected

`UndeleteInvoice: string -> string -> …` has a doc saying "supplierId, reference", which labels would
carry. F# does not accept labels in a function type inside a record field
(`error FS0010: Unexpected symbol ':' in type definition`, checked with `dotnet fsi`), so those
parameter docs stay. The same holds for `SetDefaultInvoiceCalendar`.

### What stayed

The four `ErrorAval` docs ("cleared by the next successful one") are a behaviour contract that
CLAUDE-project.md states too; the API-record docs ("the whole surface the UI is allowed to reach");
the PR-review notes on `SyncPlanRowUiType` and `SyncOutcomeRowUiType`; `FolderPickerInterop`'s note on
why `FromConverter` is needed.

## Phase 8 — `MyDogsbody.UI.Portal` and the WPF host `MyDogsbody`

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings. `dotnet test`: **1726 passed,
  0 failed, 0 skipped.** (The Thunderbird flake from phase 6 did not appear.)
- 7 files changed, +16 / −34. Classifier over the ring's `.fs` and `.cs`: 90 blocks / 302 lines →
  86 / 281 (−21). Repository-wide: 6,501 → 6,480.

### What changed

- **Two private renames, each carrying its doc.** `firstFailure` →
  `messageOfTheFirstFailedReadInTheOrderGivenOrNoneWhenEveryOneSucceeded` (4 uses; the "each read used
  to be matched on its own and all but one discarded its error" history stays above it), and
  `emptySyncView` → `syncViewHeldBeforeTheFirstLoadSyncPlanCompletesNotTheSameAsUpToDate`, whose doc
  said exactly that.
- **Openers that restated the function name were deleted**: "Builds the {invoices page, Google
  accounts browser, mail accounts browser, suppliers browser} state" above each `get…Module`
  (the `startWork` paragraph that followed each stays), "The invoices table with the window picker
  and the count line above it", and the head of `overlaySyncStatus`'s doc ("Overlays change #7's
  per-row calendar status onto a ledger row").
- **The Visual Studio `<summary>Interaction logic for MainWindow.xaml</summary>` templates** on
  `MainWindow` and `App` were deleted. Neither project generates C# XML docs.
- Two renamed call sites were wrapped; one prose mention of `firstFailure` in a doc comment was
  reworded rather than carry the long name.

### What stayed, and why

- **The host's two comments in `MainWindow.xaml.cs`** ("every service is registered by the F#
  composition root", and the F#-abbreviation-is-erased note above the `FolderPicker` registration)
  are the only account of why that file registers one service by hand; CLAUDE-project.md points at
  the second.
- **`startWork` in each page.** "Keeps API calls off the render thread. The module creator takes this
  as a parameter so tests can run the same code synchronously." `startWork` is the parameter name
  every module creator, test and CLAUDE-project.md uses; it is not renamed.
- **The `requirements.md` quotations inside components** (`GoogleAccountsComponents`,
  `MailAccountsComponents`) — each is the reason a piece of UI exists, and the ripple to name them
  would be a rewrite of the component.
- **`AssemblyInfo.cs`'s two template comments** are Visual Studio's, not this repository's.

## Phase 9 — `MyDogsbody.Tests`

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings. `dotnet test`: **1726 passed,
  0 failed, 0 skipped**, the same 1726 tests, so nothing was lost from discovery.
- 19 test files changed by this phase's script (the 15 phase-1 call-site edits are separate),
  −165 comment lines. Classifier over the ring: 1,233 blocks / 2,983 lines → 1,068 / 2,818.
  Repository-wide, `.fs` and `.cs`: 6,480 → 6,315.

### What changed: the Arrange / Act / Assert markers

218 of the 219 marker blocks (the 219th is a rationale paragraph that merely contains the word
*Assert*; it stays):

- **163 bare markers deleted** — `// Arrange`, `// Act`, `// Assert`, `// Arrange / Act`,
  `// Act / Assert`. The blank lines between a test's sections already show the structure.
- **53 labelled markers kept without their label.** `// Arrange - a real .docx with three
  paragraphs` became `// a real .docx with three paragraphs`; `// Assert - no try/with here on
  purpose: the delete must actually succeed` lost only "Assert - ". The reason after the dash is the
  part that says *why*, and it is untouched.
- **2 deleted outright:** "Assert - every field of the success output". CLAUDE.md requires every field
  to be asserted in every success test, so the sentence restated the standing rule.
- The script (`p9_markers.py`, scratchpad) preserved each line's own ending, since the tree mixes CRLF
  and LF files, and dropped a blank line only where its own deletion had made two adjacent.

### What was not done, and why

requirements.md and tasks.md item 9 also named banner dividers and descriptive comments. Neither is
safe to script, and each was measured before it was set aside.

- **Banner dividers → nested modules: not done.** 156 banner lines in 43 files. **15 of those files
  use `[<MemberData>]`**, which xUnit resolves on the class that declares the test, so moving a
  test into a nested module strands its data source in the parent. **21 contain triple-quoted or
  verbatim strings**, and the conversion re-indents everything under the banner, which would change
  their contents. 9 files have both, 6 only the first, 12 only the second, and **16 files (55 banner
  lines) have neither.** Those 16 are not blocked by either measured hazard; I did not convert them
  because two others are unmeasured — a helper declared under one banner and used under a later
  one would no longer be in scope, and every test in a converted file gets a new
  `FullyQualifiedName` — and 55 comment lines did not seem worth a re-indent of that many test
  files unreviewed. The banners stay.
- **Descriptive comments → helper names: not done.** 508 blocks the classifier files as describing a
  declaration (291) or a statement (217). 103 of them are one- or two-line docs above a private test
  helper, and I read all 103. Most are the recording fakes ("Records every save, so 'the store was
  never reached' is assertable" — 12 files) and the `withApi` / `withStore` scaffolding
  ("Fresh disposable database per test" — 8 files): each name is used at a dozen or more call sites
  per file, and the sentence carries a *why*. A long name would make every one of those call sites
  worse for a description the doc already keeps next to the definition. The rest are narration inside
  a single test — "ticking the same id again un-ticks it" — that reads a test's own assertion aloud,
  and each needs a per-case call I did not make blind. The number is the honest measure of what is
  left of the original request in the tests: about 500 blocks.

### Decisions the reader may want to overrule

1. If the banners matter more than the risk, the 16 banner files with no `MemberData` and no
   multi-line string (55 banners) are the candidates; the other 27 would need their sections
   reshaped first.
2. The 500 describing blocks in the tests are the largest remaining slice of the original request. A
   second pass over them, file by file, would be a change of its own.

## Phase 10 — rationale moved out of the source

Done because you asked for phases 4 to 10. requirements.md and tasks.md had marked this "optional,
separate change", because it changes what a reader finds in the file rather than what a thing is
called; it is recorded here as its own phase so it can be reviewed, or reverted, on its own.

### Result

- `dotnet build MyDogsbody.sln`: **0 errors**, the same two warnings.
- `dotnet test`, **three observations, all reported:**
  1. Full suite, first run: **1725 passed, 1 failed.** `ThunderbirdDatabaseContextModuleTests.
     getDatabaseContext exposes a working collection getter for every one of the five entities`,
     with the same `InvalidOperationException: Collection was modified` out of
     `BsonMapper.SerializeObject` at the warm-up line in `ThunderbirdDatabaseContextModule` as
     phase 6 — the known flake, and this phase changes nothing but comment lines.
  2. That test class alone, three runs: 2 / 2 passed each time.
  3. Full suite, second run: **1726 passed, 0 failed, 0 skipped.**
- 42 source files changed, 8 rationale documents and a README added. **No non-comment line changed
  in any of the 290 source files**: each file's code with its comment lines removed was compared with a
  snapshot taken immediately before, and all 290 are identical.
- 71 blocks, 1,049 lines → 71 one-line pointers: −978 comment lines. Repository-wide, `.fs` and
  `.cs`: 6,315 → 5,337.

### What moved, and what did not

Every comment block of **10 lines or more** was moved, verbatim, to
`docs/changes/comments-to-names/rationale/<Project>.md`, under a heading `<File>.fs: <symbol>`, and
replaced in place by one line of the same style and indentation:

```fsharp
/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ApplyTemplateWorkflow.fs: applyTemplate
let applyTemplate
```

98 blocks reached 10 lines. **71 moved. 27 did not:** 12 (160 lines) sit on a symbol CLAUDE.md or
CLAUDE-project.md names, which requirements.md rule 4 keeps; 9 (111 lines) are a module's own doc, the
first thing a reader of the file sees; 6 (80 lines) are multi-line banner dividers. Blocks under 10
lines were not touched — about 1,800 remain, and most of the rationale in the codebase is in them.
Migrations were excluded as in phase 3.

By project: `Domain` 28 blocks, `Tests` 16, `Startup` 11, `Integrations.Thunderbird` 7,
`Integrations.Google` 4, `Database` 2, `UI.Portal` 2, `UI.Types` 1.

### How it was checked, and how to undo it

- **Pointers and sections match one to one:** 71 pointers, 71 sections, none unmatched, none used
  twice.
- **Undo:** `docs/changes/comments-to-names/restore-relocated-rationale.py` puts every block back at the
  pointer's own indentation, comment style and line ending. Run against a copy of the changed files it
  restored **all 71 blocks and reproduced all 42 files byte for byte** as they were before this phase.
  Phase 10 is uncommitted and entangled in the same files as phases 1 to 9, so `git revert` could not
  separate it; the script can.
- A triple-quoted string that happens to contain lines starting `//` is never read as a comment, so
  embedded file contents in tests cannot be moved.

### Costs, stated plainly

- **The hover text is gone from those 71 declarations.** A `///` block shown in an editor tooltip is now
  a pointer to a file. For a function whose whole contract lived in that block, that is a real loss
  for the reader who is looking at the code; it is the price of the source no longer carrying
  10-to-45-line essays (median 13), which is what your first message described.
- **The docs are one folder in this change, not each originating change's `design.md`.** The
  comments cite `Q2.7`, `PR #23 review round 3`, `task 8.5` and so on, but which change owns each
  block cannot be derived mechanically. The text is verbatim so a maintainer can file it where it
  belongs; I did not guess.
- **A pointer can go stale** the way any comment can: renaming `applyTemplate` does not rename its
  heading. The one-to-one check above is a script, not a test in the suite.

### Decisions the reader may want to overrule

1. **The threshold.** At 8 lines, 109 blocks / 1,369 lines would have moved; at 12, 50 blocks / 829
   lines; at 16, 23 / 473.
2. **Whether the tests were in scope.** 16 blocks / 214 lines moved from `MyDogsbody.Tests`. They
   explain why a fixture is shaped as it is, which some readers would rather find beside the fixture.
3. **Whether to keep this phase at all.** `restore-relocated-rationale.py` makes that a one-command
   decision.
