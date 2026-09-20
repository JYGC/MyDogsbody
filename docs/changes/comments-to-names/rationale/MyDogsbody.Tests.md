# Rationale moved out of `MyDogsbody.Tests`

Comment blocks of 10 lines or more, moved here verbatim by the
`comments-to-names` change (phase 10). Each was replaced in the source by one line naming its
section below. Nothing was reworded; the text is exactly what the source held.

## `MyDogsbody.Tests/Contracts/DeleteCalendarEventDependencyContractTests.fs`

### DeleteCalendarEventDependencyContractTests.fs: RespondingHandler

Was a doc comment (`///`) at line 18.

```text
`DeleteCalendarEvent` - see `ListCalendarEventsDependencyContractTests.fs`'s own header for the
shape this suite follows: "real" means `GoogleAccountApiFactory.bindDeleteCalendarEvent` - the
stored client secret, the account's stored token, `GoogleCalendarClient.deleteEventVia` and
`GoogleAccountApiMappers.toDeleteCalendarEventError` - over a temp Google.db, with only the
calendar client's HTTP stubbed.

This is the one dependency function type this whole change treats with the most caution
(tasks.md's own words): a defect here can delete a calendar entry the application neither owns
nor can restore. Live verification against Google, including a real delete, is recorded as
manual coverage in outcome.md - this suite only proves the composition root's own translation
of what Google answers.
```

## `MyDogsbody.Tests/Contracts/GetCurrentTimeContractTests.fs`

### GetCurrentTimeContractTests.fs: assertClockProperties

Was a comment (`//`) at line 7.

```text
friction #15 - the clock's contract suite, stated rather than skipped.

GetCurrentTime is a dependency function type, and CLAUDE.md calls those published interfaces
owing a suite run against the real implementation AND every fake. The real implementation is
`fun () -> DateTime.Now` (bound in Startup.fs), whose whole nature is to return something
different each call - so "assert the real side and the fake side agree" has no meaning.

What this suite asserts instead, for the real clock and every fake alike:
  1. two successive calls are non-decreasing;
  2. the Kind is what the composition root promises (Local, because it binds DateTime.Now).
For the real clock only, (3) the value is within a tolerance of DateTime.Now at test time.

The part with ACTUAL LOGIC - the cutoff arithmetic (start-of-day, N days back, the same value
at 09:00 and 17:00) - is unit-tested against fixed instants in ScanForInvoicesWorkflowTests
(task 3.1), which is where the behaviour worth testing lives.
```

## `MyDogsbody.Tests/Contracts/ListCalendarsDependencyContractTests.fs`

### ListCalendarsDependencyContractTests.fs: RespondingHandler

Was a doc comment (`///`) at line 18.

```text
`ListCalendars` is the one dependency function type in this change whose real implementation
is a network service (friction #2). Per design.md's arrangement, "real" here means
`ListCalendars` exactly as the composition root binds it - `GoogleAccountApiFactory.bindListCalendars`:
the stored client secret, the account's stored token, `GoogleCalendarClient.listCalendarsVia` and
`toListCalendarsError` - over a temp Google.db, with only the calendar client's HTTP stubbed. That
exercises the adapter's own request-building, paging and response-parsing, and the translation
the page's alerts are written from. Live verification against Google itself is recorded as manual
coverage in outcome.md, not silently skipped.

Until PR review series 2 round 7 the "real" side here bound `listCalendarsVia` to a copy of that
translation written in this file, which never read the store and had no `CalendarApiNotEnabled`
or `ClientSecretInvalid` case. With the factory's translation of Google's answers swapped for the
store's, every test in the suite still passed.
```

## `MyDogsbody.Tests/Contracts/MessageNormalizationContractTests.fs`

### MessageNormalizationContractTests.fs: valueOrFail

Was a comment (`//`) at line 9.

```text
MessageNormalization.normalizeMessage is the SOLE DOOR to NormalizedMessage: the record is
private to the Invoices namespace and this is the only function that builds the literal.
ApplyTemplateWorkflow is then written against a guarantee it never re-checks - that anything
holding this type has had its subject, its attachment filenames and both views of every part's
lines put through TextNormalization exactly once.

That guarantee is what this suite pins. CLAUDE.md -> Testing -> Contract asks for every mapper
at a ring boundary to be asserted field-for-field, and normalizeMessage is that mapper for the
Invoices area's stage boundary: ScannedMessage (untrusted text, straight off a reader) ->
NormalizedMessage (the only thing the engine accepts). Its own unit tests assert what it does
with a given input; these assert the properties applyTemplate LEANS on, so a later change that
reaches NormalizedMessage by another route - a second constructor, a store loading one back,
a normalization step moved elsewhere - breaks here rather than silently in the engine. The
un-normalized-subject defect of review round 1 was exactly that drift, found by hand.

DEFERRED, deliberately, and not silently: the error-translation rows (each InvoiceError and
TemplateError case to its intended MyDogsbodyException) need TemplateApiMappers, which lands in
PR #12 with the store and the API record. A dependency-function-type suite is likewise thin
value while the Invoices area still declares no dependency types and has no adapter to run one
against - change #4 introduces both, and owes the suite then.
```

## `MyDogsbody.Tests/Database/MigrationTestHelpers.fs`

### MigrationTestHelpers.fs: withTempDatabaseAndPath

Was a doc comment (`///`) at line 11.

```text
Fresh temp database per test. `Foreign Keys=True` on the connection string matches
DatabaseContextSetup.fs - PRAGMAs are per-connection and off by default, so this is what makes
every connection opened against this file self-apply FK enforcement, rather than every write
helper having to remember to prepend the PRAGMA by hand. `Pooling=False` also matches
DatabaseContextSetup.fs: without it a pooled connection keeps the file locked on Windows after
dispose, which is what drove the harnesses to the process-global pool clear (the ClearAllPools hammer)
- see docs/changes/sqlite-pool-flake. With pooling off, `use connection` releases the handle
and the delete just works.

Hands back the file path as well as the connection string - DatabaseContextSetup.createDatabaseContext
takes a path, not a connection string, so the one caller that exercises it needs both.
`withTempDatabase` below is the same setup/teardown for the (more common) callers that only
need the connection string.
```

## `MyDogsbody.Tests/E2E/GoogleAccountsFlowTests.fs`

### GoogleAccountsFlowTests.fs: handOffWork

Was a doc comment (`///`) at line 82.

```text
Work handed off where it is started, the way production's `startWork` (`Async.Start`) hands it
to the thread pool - then run by the test itself, on its own thread, when it calls
`runHandedOffWork`. `pendingWork` says how much is waiting.

The two confirmation flows need this rather than `fun work -> work ()`. MudBlazor completes a
message box's result on the renderer's dispatcher and the code awaiting it resumes right there,
so work run where it is started would run the removal - the store, the reload, every re-render -
on the dispatcher. bUnit runs each `WaitForAssertion` check on that same dispatcher, with a
one-second timeout. Whenever the dispatcher was busy as "Remove" was clicked, the click was
queued instead of running on the test's thread, `Click()` returned at once, and the check waited
behind the whole removal: under the full suite's load it failed with "Check count: 0", the check
never having run at all.
```

## `MyDogsbody.Tests/E2E/GoogleAccountsTestHarness.fs`

### GoogleAccountsTestHarness.fs: GoogleAccountsHarness

Was a doc comment (`///`) at line 16.

```text
A bUnit TestContext subclass wired for MudBlazor (AddMudServices, JSRuntimeMode.Loose), over
the Google integration's own LiteDB database.

The API record is composed here rather than by `GoogleAccountApiFactory.createGoogleAccountApi`,
because the real consent flow needs a system browser and the real calendar client needs a
network connection - neither of which any test may require (tasks.md's own header rule). What
goes into it is what production runs:

- `RegisterAccount`, `GetCalendarsFor` and `SetDefaultInvoiceCalendar` are the factory's own
  compositions (`GoogleAccountApiFactory.registerAccountWith`, `getCalendarsForWith` and
  `setDefaultInvoiceCalendarWith`), handed the two fakes in place of the real consent flow and
  calendar client. They used to be re-composed here, and a factory whose `RegisterAccount` never
  discarded a refused registration's token passed every flow.
- The storage-only members are composed here over the factory's own bindings
  (`GoogleAccountApiFactory.bind*`, since PR review series 2 round 8), with the real domain
  workflows and error translation, over the real LiteDB context.

Only the two network-touching dependencies are test-controlled fakes, matching the same seam
`GoogleAuthorizationTests`/`GoogleAccountApiFactoryTests` already exercise.
```

## `MyDogsbody.Tests/E2E/InvoiceSyncFlowTests.fs`

### InvoiceSyncFlowTests.fs: a delete is listed in the plan before anything runs, and the button defers to the page's own gate rather than 

Was a doc comment (`///`) at line 121.

```text
Q2.13/task 8.5: a delete never runs the instant "Sync now" is pressed - the page interposes a
confirmation, naming what would be deleted, and only calls ExecuteSync if the user accepts it
(InvocesPage.confirmAndExecuteSync). Driving MudMessageBox's own rendered Yes/Cancel buttons
through bUnit needs the click to happen WHILE the dialog's awaited Task is still pending - on a
second, unblocked interaction, not a synchronous `.Wait()` on the test thread, which deadlocks
against the same thread that would have to render and click it. No confirmation dialog in this
codebase is exercised that way today (not even the pre-existing per-row `confirmAndDelete`), so
this test follows the same precedent: it proves the plan lists the delete BEFORE anything runs
(Q2.13's actual requirement), that "Sync now" invokes the page's gate rather than executing
directly (`onSyncRequested` here only records that it was called, and deliberately does NOT
call `executeSync` itself), and that the underlying mechanism - once actually told to run -
really does delete the event. `InvoicesPage.confirmAndExecuteSync`'s own gating logic (skip the
dialog only when there is no delete in the run; otherwise show it and call `ExecuteSync` only
on confirmation) was verified by direct code review rather than by a bUnit dialog click.
```

## `MyDogsbody.Tests/E2E/MailAccountsFlowTests.fs`

### MailAccountsFlowTests.fs: root

Was a comment (`//`) at line 380.

```text
requirements.md -> "Walking the chosen folder": "WHEN several profiles are found THE SYSTEM
SHALL list all of their accounts, QUALIFIED BY THE PROFILE PATH THEY CAME FROM, so two
profiles containing the same account are distinguishable (Q4.9)" - and again under "Edge
cases": "WHEN two profiles declare accounts with the same email address THE SYSTEM SHALL list
both, qualified by profile path."

The chosen folder being "a backup copy" alongside the live profile is one of the three
shapes the walk is required to handle, so this is the ordinary case, not a contrived one.
Both rows carry the same display name, the same address, the same format, the same folder
count and the same size; the only thing that separates them is the profile - and the user is
being asked to pick ONE of them for import.
```

## `MyDogsbody.Tests/Integrations/Google/GoogleCalendarClientTests.fs`

### GoogleCalendarClientTests.fs: capturedRequestMethod

Was a comment (`//`) at line 649.

```text
Google Calendar's events.update is a full-resource PUT: any field the request body does not
set is CLEARED server-side, not left untouched - contrast events.patch, which
Google.Apis.Calendar.v3's own XML doc calls out as supporting "patch semantics", wording it
uses for no other Events method. buildAllDayGoogleEvent only ever sets Summary, Description,
Start and End (Q2.14's "title and date"), so issuing this as a PUT would silently wipe
extendedProperties - the InvoiceSyncKey createEvent stamped on the event - and its reminders
override, on the event's very first update. The next sync would then read the event back as
keyless (an unresolvable orphan, per InvoiceSyncApiMappers.toOrphanedEvents) and read the
invoice as unsynced (a fresh, duplicate CreateEvent) - exactly the duplicate requirements.md
says the extended property exists to prevent ("the extended property was chosen so a rename
would not cause a duplicate"). PATCH sends the same fields but merges rather than replaces,
so extendedProperties and reminders survive untouched.
```

## `MyDogsbody.Tests/Startup/InvoiceSyncApiMappersTests.fs`

### InvoiceSyncApiMappersTests.fs: invoice

Was a comment (`//`) at line 87.

```text
requirements.md's edge case: "WHEN the same invoice has two events on the calendar THE
SYSTEM SHALL update the first and report the second as a duplicate, and SHALL NOT delete it
without confirmation." `diff` compares only the first-seen event for a shared key against
the ledger (its own "Duplicates" comment) and leaves every other event untouched - neither
updated nor deleted, so it is never the target of any SyncAction. Before round 2's fix, that
made the second event invisible here too. Before round 3's fix, it was visible but
mislabelled: OrphanedEventUiType's old bare `NeedsAttention: bool` could not tell this case
apart from a genuinely keyless event, so the screen told the user "no recognisable sync key"
for an event that had one - just not the one `diff` matched. This asserts the exact reason,
not merely that the row is flagged.
```

### InvoiceSyncApiMappersTests.fs: invoice (2)

Was a comment (`//`) at line 116.

```text
PR #23 review round 5. Q2.9: "narrowing hides; it does not forget" - an invoice's window
membership is decided by when its MESSAGE arrived (MyDogsbody.Database/InvoiceStore.fs's
cutoff filters on MessageReceivedAt), not by its due date, so an invoice can sit outside
`InWindow` today while its due date still falls inside the calendar's queried date range
(e.g. a 45-day-old invoice due next week, viewed under a 14-day window). `diff` correctly
produces NO action at all for such a key: not Create/Update/LeaveAlone (the invoice is not
in `InWindow`) and not Delete either (the key is still in `AllLedgerKeys`, since the invoice
has not left the ledger - merely fallen out of view). Before this round's fix,
toOrphanedEvents had no way to tell "nobody's SyncAction named this event because it is out
of view" apart from "a second event is sharing an already-matched key", and reported every
such event as DuplicateOfAnotherEventsSyncKey - a false "needs attention" alarm on a
perfectly healthy, singly-synced event, purely from narrowing the scan window.
```

## `MyDogsbody.Tests/Startup/TemplateApiFactoryTests.fs`

### TemplateApiFactoryTests.fs: a cleared FixedValue box is stored as an empty fixed value and never written to the log

Was a doc comment (`///`) at line 227.

```text
The one rule shape that used to break the rule above. `FixedValue null` - a cleared
MudTextField, the same input TemplateApiMappers already guards for an AsMoney separator - is
the only text-carrying rule kind validateTemplate lets through, so it reached
TemplateFieldRules' CHECK (RuleText IS NOT NULL) and came back as an infrastructure failure.
Measured against HEAD before the fix: Error "Failed to insert new template." with exactly one
entry in `logged`, for what is a validation-shaped input.

The template is stored rather than refused, because that is exactly what an empty box already
does: `FixedValue ""` saves today and ApplyTemplateWorkflow reports it as the rule finding
nothing, with its own sentence in toMatchedNothingReason.
```

### TemplateApiFactoryTests.fs: every TemplateApi member reports its own declared ActionName

Was a doc comment (`///`) at line 790.

```text
Every member's ActionName, in one place, driven through the real API.

The action is the only thing an exception-log row carries that says WHICH call failed, and
ActionNames entries are `$"..."`-composed and compiler-unchecked, so nothing but a test stops
one member reporting another's - CLAUDE-project.md -> Testing -> Contract states the rule
("assert each outer-ring function's error reports its declared action. A typo is otherwise
invisible until someone reads the exception log").

Five of the six members happened to be pinned by an ad-hoc assertion in the tests above;
EditTemplate was not, and repointing its mapError to ActionNames...addTemplate passed the whole
suite, 778 of 778. The structural suite in Contracts/ActionNamesTests.fs cannot catch that: it
checks that each string ends with the name of the binding that declares it and that no two
bindings share one - both of which stay true when the wrong binding is USED.

Counted against the record's own field count by reflection, so a seventh member added later
fails here until it names its action.
```

## `MyDogsbody.Tests/Startup/TemplateApiMappersTests.fs`

### TemplateApiMappersTests.fs: toUnvalidatedTemplate reads a cleared FixedValue box as an empty fixed value rather than a null one

Was a doc comment (`///`) at line 132.

```text
The same cleared-MudTextField class as the AsMoney guard above, arriving on the OTHER string a
rule carries. FixedValue is the one text-carrying rule kind nothing downstream refuses a null
for: AfterLabel and LinesAfterLabel are caught by
validateOneRuleOnItsOwnAndAddItsCompiledPatternToTheMap's LabelIsEmpty check, and
RegexCapture / SubjectCapture / AttachmentName by compilePattern's own isNull guard - but
`FixedValue null` passes validateTemplate, reaches TemplateRecordMappers.toFieldRuleColumns as
`Some null`, and is written as RuleText = NULL, which TemplateFieldRules' CHECK constraint
refuses. Measured against HEAD before this test: AddTemplate came back "Failed to insert new
template." AND wrote one entry to the exception log, for a user who merely emptied a text box.

A cleared box and an untouched empty box are the same user action, so they become the same
rule. `FixedValue ""` is deliberately savable - ApplyTemplateWorkflow.foundUnlessEmpty reports
it as the rule finding nothing and toMatchedNothingReason gives it its own sentence - so this
normalises to that established behaviour rather than inventing a second one for null.
```

### TemplateApiMappersTests.fs: every FieldRule kind survives the round trip carrying its own UI columns

Was a doc comment (`///`) at line 221.

```text
toFieldRuleUiColumns decides which kind string a stored rule is SHOWN and re-saved as, and the
editor edits what it is shown: EditTemplate maps the shown string straight back to a FieldRule,
so a kind swapped between two cases does not merely mislabel a row - the next save silently
rewrites what the rule reads. SubjectCapture reads the subject and AttachmentName the filename,
so that particular swap changes the answer on every message, forever, with nothing to notice it
by.

Measured before this test existed: swapping SubjectCapture's and AttachmentName's kind strings
passed the whole suite, 773 of 773. Only five of the seven kinds were round-tripped anywhere.

One row per FieldRule case, counted against the union by reflection, so an eighth kind fails
here rather than being round-tripped by nothing.
```
