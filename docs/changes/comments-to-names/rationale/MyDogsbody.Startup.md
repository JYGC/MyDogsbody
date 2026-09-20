# Rationale moved out of `MyDogsbody.Startup`

Comment blocks of 10 lines or more, moved here verbatim by the
`comments-to-names` change (phase 10). Each was replaced in the source by one line naming its
section below. Nothing was reworded; the text is exactly what the source held.

## `MyDogsbody.Startup/InvoiceSyncApiFactory.fs`

### InvoiceSyncApiFactory.fs: selectedSyncKeys

Was a comment (`//`) at line 213.

```text
Q2.7: the selection when there is one, everything outstanding when there is not.
Selected by SyncKey (supplier + reference, InvoiceSyncKey.value) - the one field
every plan-row kind carries that is actually unique - rather than by re-trusting
an event id or invoice id the preview handed back, so a plan that has moved on
since the preview (a rescan, a due-date change) still selects the right CURRENT
action for that invoice rather than a stale one.

NOT Reference alone (PR #23 review round 1): the ledger's unique index is on
(supplier, reference), not on reference alone, so two different suppliers can
share the same reference text. Matching on bare Reference let a tick on one
supplier's row also select a different supplier's action for the same text -
including, in the worst case, a DeleteEvent nobody ticked.
```

## `MyDogsbody.Startup/InvoiceSyncApiMappers.fs`

### InvoiceSyncApiMappers.fs: toOrphanedEvents

Was a doc comment (`///`) at line 107.

```text
Events the plan does not fully explain: no sync key at all (an orphan needing attention), one
whose invoice has left the ledger (a pending delete, already shown in the plan too), or a
second event sharing another event's key (a duplicate needing attention - requirements.md's
"the same invoice has two events on the calendar" edge case).

`DiffInvoicesAgainstCalendarWorkflow.diff` compares only the first-seen keyed event against
the ledger for a shared key and leaves every other one untouched - neither updated, left
alone, nor deleted (its own "Duplicates" comment) - so a duplicate is never the target of any
SyncAction and so never appears in `matchedEventIds` below. Left out of this function, such an
event was invisible everywhere in the view: not in the plan, not among orphans, PR #23 review
round 2.

A THIRD reason an event can be unmatched, distinct from a duplicate: its invoice is still in
the ledger but merely outside the current scan window (Q2.9's "narrowing hides; it does not
forget") - `InWindow` membership is decided by when the invoice's MESSAGE arrived, not by its
due date (`MyDogsbody.Database/InvoiceStore.fs`'s cutoff filters on `MessageReceivedAt`), so an
invoice can sit outside `InWindow` while its due date still falls inside the calendar's queried
date range. `diff` then produces no action at all for that key: not a create/update/leave-alone
(not `InWindow`) and not a delete either (the key is still in `AllLedgerKeys`). `explainedKeys`
below is what tells the two apart: a genuine duplicate's key IS explained elsewhere, via the
OTHER (matched) event sharing it, whereas an out-of-window event's key is not explained by
anything in the plan at all. Before this round's fix (PR #23 review round 5), every
out-of-window event was misreported as a duplicate, purely because narrowing the window moved
its invoice out of `plan`.
```

## `MyDogsbody.Startup/TemplateApiFactory.fs`

### TemplateApiFactory.fs: splitPastedTextIntoLines

Was a doc comment (`///`) at line 21.

```text
Splits pasted test-panel text into TextLines the way any plain-text reader would: one line
per newline, BlockIndex incrementing each time a run of blank lines is crossed - "plain text
splits on blank lines" (Documents/DocumentsTypes.fs's own TextLine doc comment). The blank
lines themselves are still emitted; TextNormalization.normalize drops them later, the same way
it would for any other reader's output. Pure, no mutable state.

Null degrades to empty, the same way TextNormalization.normalizeText's own first line does and
for the same reason: a cleared MudTextField hands a bound `string` back as null, running the
panel before pasting anything is the first thing a user does, and String.Replace on null raised
NullReferenceException out of an API whose type promises a Result - on a path the UI calls from
Async.Start, where neither an alert nor the log would ever see it. Empty text yields one blank
line, which normalization drops, so every rule reports finding nothing rather than the panel
disappearing.
```

### TemplateApiFactory.fs: toTestMessage

Was a doc comment (`///`) at line 47.

```text
Builds the ScannedMessage TestTemplate applies against: the pasted subject, the sample
filename, and the pasted text ON THE PART THE TEMPLATE ACTUALLY READS.

Which part that is, is not cosmetic. ApplyTemplateWorkflow.partMatchesSelector gives an
Attachment-scoped template no sight of BodyPart at all, so pasted text parked on the body made
the panel report every field of a perfectly sound template as unmatched - and a PDF attached to
an email is the primary case this change exists for, so that is the template most likely to be
tested and the one the panel was least able to answer. Body and AnyPart both read the body, so
only the Attachment case moves.

An Attachment-scoped template gets its part whether or not a filename was supplied: the
filename is what an AttachmentName rule reads, not what makes the document exist. Under Body
and AnyPart the attachment part is still text-free and still appears only when a filename was
given - AttachmentName matches a filename, not content, and duplicating the sample text onto a
second part AnyPart already selects would offer the same label twice.
```

### TemplateApiFactory.fs: theReasonThisFieldsRowCarriesOutOfTheSingleErrorAWholeRunHandsBack

Was a doc comment (`///`) at line 106.

```text
ApplyTemplateWorkflow stops at the first field that fails, so one InvoiceError stands for the
run rather than for every field in it. Giving it to every row reported a template's sound rules
as broken and quoted another field's diagnosis at them - measured, an "Invoice:" rule that had
just extracted INV-9001 was shown as failed with "The rule for Amount found nothing in the
sample text.", and a FixedValue Currency rule, which cannot fail at all, got the same. That is
the wrong rule to send the author to, on the one screen the change exists to let them check
extraction against. The fields the run never faulted say only that: the run stopped elsewhere.

They still report Succeeded = false, because no value came back for them either - claiming
success with nothing to show would be the same lie from the other side.
```

### TemplateApiFactory.fs: TestTemplate =

Was a comment (`//`) at line 260.

```text
Runs the same engine a scan calls - ValidateTemplateWorkflow then ApplyTemplateWorkflow
- over pasted text, never writing anything.

The payment term is the SUPPLIER'S, read through the same dependency AddTemplate uses.
It was a hard-coded 0, on the stated grounds that "the test panel has no supplier context
of its own to read one from" - which is not so: the input carries the SupplierId, and
loadSuppliersForTemplates is bound right here. With 0, DateFromField derived a due date
equal to its source and reported it as a SUCCESS, so the panel showed the author a due
date their template will never produce, on the one screen the change exists to let them
check extraction against, and for the single rule requirements.md calls "the rule that
carries extraction from 12% to 39%". requirements.md is explicit in both directions:
the derivation adds "the SUPPLIER'S payment term", and testing the template must PROVE
DateFromField produces the due date its documents never state.

Reading the term means the supplier has to exist, so an unknown one is reported the way
AddTemplate reports it rather than silently testing against an invented term. Still no
write - this reads suppliers and stores nothing.
```

### TemplateApiFactory.fs: rules

Was a comment (`//`) at line 311.

```text
Only the fields the template actually carries a rule for. IssueDate and
DueDate are optional - ValidateTemplateWorkflow requires a rule only for
Reference, Amount and Currency - so a template that declares no date rule
has not failed to extract a date, it was never asked for one. Reporting the
absent fields anyway told the author that two fields of a sound template
were broken, which is the same false negative the attachment-part fix
closed, arriving from the other direction.

The ORDER stays the engine's fixed evaluation order rather than the rule
list's, so the panel reads the same way whatever order the rules were
entered in; only the fields with no rule drop out.
```

## `MyDogsbody.Startup/TemplateApiMappers.fs`

### TemplateApiMappers.fs: toFieldRule

Was a doc comment (`///`) at line 71.

```text
The FixedValue branch is the second place a cleared MudTextField had to be named, for the same
reason as the AsMoney separator below - and it is the only rule kind where a null gets past
every later check. `AfterLabel`/`LinesAfterLabel` reach ValidateTemplateWorkflow's
IsNullOrWhiteSpace label guard (LabelIsEmpty) and the three pattern kinds reach compilePattern's
own isNull guard (PatternInvalid); a `FixedValue null` passes validateTemplate untouched, is
written by TemplateRecordMappers.toFieldRuleColumns as `Some null`, and lands on
TemplateFieldRules' CHECK (RuleText IS NOT NULL). Measured: AddTemplate answered "Failed to
insert new template." and wrote one entry to the exception log, for a user who emptied a box -
an infrastructure failure reported for a validation-shaped input, on the one path
CLAUDE-project.md requires to stay unlogged.

Normalised to "" rather than refused, because an empty box already means something settled here:
`FixedValue ""` saves, ApplyTemplateWorkflow.foundUnlessEmpty reports it as the rule finding
nothing, and toMatchedNothingReason gives it its own sentence. A cleared box and an untouched
one are the same user action, so they become the same rule. Only this branch is normalised -
blanket-normalising ruleText would turn a null pattern's "Pattern must not be empty." into the
vaguer "needs a capture group.", since Regex("") compiles.
```

### TemplateApiMappers.fs: toFailingField

Was a doc comment (`///`) at line 269.

```text
Which field an apply-time error is ABOUT.

ApplyTemplateWorkflow evaluates the fields in one fixed order and stops at the first that
fails, so a whole test-panel run hands back a single InvoiceError - and a panel that gave that
one error to every row told the author "Reference failed" about a rule that had just matched,
with a reason naming Amount. requirements.md asks the panel to show, per field, "where it
failed - why", and the panel's entire job is to send the author to the rule that is broken; the
reason has to land on the field it names.

DueDateOutOfRange carries a TemplateId rather than a field, but it is raised in exactly one
place - the DueDate DateFromField branch - so the field is DueDate by construction. The three
supplier/template-selection cases name no field and cannot arise from a panel run at all, so
they answer None and their sentence goes on every row, as before.

A match rather than a catch-all, for the same reason toFieldFailureReason below is one: an
InvoiceError case added later breaks the build here rather than quietly answering None.
```

### TemplateApiMappers.fs: toMatchedNothingReason

Was a doc comment (`///`) at line 310.

```text
The sentence for "the rule yielded nothing", naming the input that rule actually reads.

Three of the seven kinds do not read the pasted sample text, and requirements.md is explicit
about each: SubjectCapture runs its pattern "against the message subject, not the document
body", AttachmentName "against the attachment's filename, not its content", and FixedValue
returns its value "without consulting the text at all". One sentence naming the sample text for
every kind therefore sent the author to a box that is not the problem - measured with an empty
subject box and a sample text full of matching content, an unmatched SubjectCapture rule read
"The rule for Reference found nothing in the sample text." That is the wrong box to send them
to, on the one screen the change exists to let them diagnose a template, and it contradicts the
rule kind's own documented behaviour.

A FixedValue rule has no input at all, so it gets a sentence of its own rather than a locative:
the only way it yields nothing is an empty fixed value, which is the thing to go and fix.

None is a caller with no rule to hand - an error naming no field - and keeps the original
wording. Exhaustive over FieldRule, so an eighth rule kind breaks the build here rather than
quietly claiming to read the sample text.
```

### TemplateApiMappers.fs: toFieldFailureReason

Was a doc comment (`///`) at line 338.

```text
The other outbound translation: an InvoiceError becomes the sentence the test panel prints
against a field. InvoiceError carries no ActionName and never reaches handleError in this
change (design.md -> Errors), so it does not go through toMyDogsbodyException - but it is
still a domain error the user reads, and the same rule applies: a sentence, not a union.

`string error` was what this replaced, and it printed
`TemplateMatchedNothing (TemplateId "test", Amount)` - a union dump quoting the placeholder
TemplateId the composition root invents for a run that never touches the store. requirements.md
asks the panel to say, where a field failed, WHY; the id of a template that does not exist is
not part of that answer, so every case below drops it and names the field instead.

A match rather than a catch-all: an InvoiceError case added later breaks the build here, the
same way toMyDogsbodyException's exhaustiveness is what caught five missing TemplateError
branches in the first review round.
```
