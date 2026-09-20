# Rationale moved out of `MyDogsbody.Domain`

Comment blocks of 10 lines or more, moved here verbatim by the
`comments-to-names` change (phase 10). Each was replaced in the source by one line naming its
section below. Nothing was reworded; the text is exactly what the source held.

## `MyDogsbody.Domain/Calendar/CalendarDateRangeWorkflow.fs`

### CalendarDateRangeWorkflow.fs: derive

Was a doc comment (`///`) at line 6.

```text
Q2.5. Mirrored around today rather than applied backwards only: the scan window looks
BACKWARDS at when mail ARRIVED, but an invoice event sits on its DUE date, normally ahead of
that. The
range is then stretched to cover the earliest AND latest due date actually in view, in
whichever direction each needs - a supplier on 60-day terms inside a 14-day scan window would
otherwise fall outside a [today-14, today+14] range on the far end, and an invoice that was
already overdue when it was scanned (a due date earlier than today-14 - not exotic for a
recurring bill whose due date precedes the notice email) would fall outside it on the near
end. Either direction reads a still-live event as missing and creates a duplicate for it, so
requirements.md's "never produce a range that excludes an invoice in view" is unconditional,
not forward-only, and both ends of the range widen independently to honour it.
```

## `MyDogsbody.Domain/Calendar/RegisterGoogleAccountWorkflow.fs`

### RegisterGoogleAccountWorkflow.fs: registerGoogleAccount

Was a doc comment (`///`) at line 6.

```text
Refuses before opening the browser when the registration cannot possibly succeed regardless
of which account is chosen - no client secret has been supplied - because completing consent
only to be refused afterwards wastes the user's time and leaves a granted scope with nothing
to show for it.

`AccountAlreadyRegistered` cannot be checked before that point: which account is a duplicate
is only known once `authoriseAccount` has returned an email, so that check runs immediately
afterwards, before anything is saved. This is a deliberate reading of design.md's sequence
diagram, which shows the check ahead of the browser step for narrative grouping rather than
as an achievable call order - a browser-issued email cannot be compared before the browser
step has produced one.

That ordering is what `discardAuthorisation` pays for. Consent has already persisted a token
against the id `authoriseAccount` returns by the time the duplicate is found, so refusing by
simply not saving would leave that token behind with no account row pointing at it - and the
only thing that deletes a token is removing the account it belongs to. Every refused
duplicate would strand another one.

The duplicate check is only the *first* step that can fail after consent, not the only one:
reading the existing accounts and saving the new one can both fail too, and each strands the
same token for the same reason. So the discard covers the whole post-consent tail rather than
one branch of it - anything that leaves without an account row hands the token back.

The one post-consent step this workflow cannot see is inside `authoriseAccount` itself:
reading the account's email once consent has already written the token. An `Error` from it
carries no id to discard, so that step is the adapter's to clean up - see `AuthoriseAccount`.
```

## `MyDogsbody.Domain/Calendar/SetClientSecretWorkflow.fs`

### SetClientSecretWorkflow.fs: setClientSecret

Was a doc comment (`///`) at line 5.

```text
Stores the application-wide OAuth client secret.

Refuses a blank one without reaching the store. The page, and `RegisterGoogleAccountWorkflow`'s
"no client secret" check, both read a stored secret as one that has been supplied - so a blank
save read back as a secret: "Add account" enabled, and registering failed at the authorisation
call as "malformed", which requirements.md's "say so and disable account registration" rules
out. Refusing it here also keeps a working secret from being replaced by nothing.

Anything else is stored exactly as given. Whether it parses is the adapter's to report, when it
is used ("The stored Google client secret is malformed.").
```

## `MyDogsbody.Domain/InvoiceTemplates/InvoiceTemplatesTypes.fs`

### InvoiceTemplatesTypes.fs: TemplateError

Was a doc comment (`///`) at line 103.

```text
Can this template be SAVED? Apply-time failures are InvoiceError, not this - see design.md ->
Decisions taken.

Three cases beyond design.md's original listing, the same way change #1 added
SupplierError.PaymentTermInvalid when its documented DU had no case for a failure the
workflow actually needed to report:
 - TemplateSupplierIdInvalid, TemplateIdInvalid: design.md's sequence diagram never shows a
   "parse this id" step, but EditSupplierWorkflow.validate is the established precedent for
   this exact shape of problem (an id arriving as an untrusted string) - it parses the id
   itself and reports SupplierIdInvalid on failure. These mirror that, one for the supplier a
   template belongs to and one for the template being edited.
 - DerivationSourceIsSelf: "a DateFromField rule may not name DueDate as its own source"
   (design.md -> Decisions taken, #9) names a refusal that fits neither
   DerivationSourceMissing (the source DOES have a rule - itself) nor DerivationSourceNotADate
   (the question isn't whether the source is date-shaped). Generalised to any field, not only
   DueDate, since a rule naming itself as its own source is circular regardless of which field.
 - ReorderIncomplete: requirements.md requires refusing a reorder that omits one of the
   supplier's existing templates. A submitted id that names no template of this supplier's
   reuses TemplateNotFound (an exact semantic fit); an existing template left out of the
   submitted order has no existing case to reuse, so this one carries every id left out, not
   only the first, the same way MultipleSuppliersMatched carries every match rather than one.
 - ReorderDuplicate: an order naming the same template twice is neither foreign (the id IS the
   supplier's) nor incomplete (Set.ofList collapses the repeat, so nothing looks missing), so
   neither existing case describes it. Carries the repeated id, the same way TemplateNotFound
   carries the offending one.
 - DerivationUnsupported, FieldHintMismatch: both close a save-time gap that used to surface
   as a scan-time silence. The engine supports exactly one derivation (DueDate from IssueDate)
   and reads a date only from an AsDate hint and money only from AsMoney; a template naming a
   different derivation, or pairing a date field with AsText, previously saved without
   complaint and then extracted nothing on every message forever. requirements.md is explicit
   that a template is validated "at that moment, not when a scan next runs", so the refusal
   belongs here. DerivationUnsupported carries both ends of the derivation because the message
   has to name the pair; FieldHintMismatch carries the hint actually given, since "which hint
   did I choose?" is the question the user needs answered.
 - LabelIsEmpty: "".IndexOf returns 0 against any string, so an AfterLabel with an empty label
   matches the FIRST LINE of every document and returns the whole of it, and
   LinesAfterLabel("", 1) returns the second. Both then pass the engine's empty check and store
   a confidently wrong value - worse than the two cases above, which merely drop a field. Null
   counts as empty here: a label arrives from a stored row, and a NULL column is how one
   reaches the domain.
 - RuleUnreachableForPart: an AttachmentName rule on a Body-scoped template reads a list of
   filenames that is empty by construction, so the field is silently absent on every message
   forever. validateTemplate sees both the Part and the Rules, so the contradiction is
   refusable at the only moment the user is standing in front of the editor. Carries both ends
   for the same reason DerivationUnsupported does - the message has to name the pair.
```

## `MyDogsbody.Domain/InvoiceTemplates/ReorderTemplatesWorkflow.fs`

### ReorderTemplatesWorkflow.fs: ensureTheSubmittedOrderDoesNotNameTheSameTemplateTwice

Was a doc comment (`///`) at line 18.

```text
Neither check below can see a repeat: Set.ofList collapses the repeat, so
ensureNoneOfTheSuppliersExistingTemplatesWasLeftOutReportingEveryOneLeftOutNotOnlyTheFirst's
difference stays empty, and the duplicate is one of the supplier's own templates so
ensureEverySubmittedIdNamesATemplateOfThisSupplierReportingTheFirstThatDoesNot is satisfied too.
design.md specifies reorder as one UPDATE ... SET Position per template, so [1;1;2;3] would
write four
positions for three templates - template 1 at 0 and again at 1, then 2 and 3 at 2 and 3 -
leaving nothing at position 0 and silently reshuffling which template an invoice is matched
against first, while reporting success.

A property of the submitted list alone, so it is decided before the store is read.
```

## `MyDogsbody.Domain/InvoiceTemplates/TextNormalization.fs`

### TextNormalization.fs: normalizeText

Was a doc comment (`///`) at line 59.

```text
NFKC is the one step here that is not total. String.Normalize raises ArgumentException for any
string carrying an unpaired surrogate, and the input is text extracted from PDFs and email
bodies - the least trustworthy source in the system, where a truncated or mis-decoded
extraction is exactly how a lone surrogate arrives. IsNormalized raises on the same input, so
checking first would not avoid the try.

normalize promises TextLine list -> TextLine list with no failure channel, and an exception
escaping here would unwind out of the domain, past a composition root that maps values rather
than catching, into a UI with no alert for it - which CLAUDE.md rules out in either ring. So a
line that cannot be normalized degrades to its un-normalized self: one malformed glyph costs
that line its NFKC folding rather than taking down the whole scan. The remaining steps are
per-character and total, so they still apply to it.
```

### TextNormalization.fs: NormalizedLine

Was a doc comment (`///`) at line 85.

```text
One normalized line, together with the lines the document actually laid out to produce it.

Segments is what makes a line-oriented rule possible at all on top of the continuation join.
The join is right for reading text - a hard-wrapped label has to be findable - but it is wrong
for COUNTING lines, and LinesAfterLabel counts them. A value on its own line is
indistinguishable from a wrapped continuation (both start lower-case under a predecessor that
ends in no sentence terminator), so joining silently shifted every offset below a bare label
whose value happened to start lower-case: "Reference" / "wu-88213" merged, and
LinesAfterLabel("Reference", 1) then returned the line AFTER the value. Whether a template
worked depended on the case of the first character of a value its author does not control.

A line that absorbed no continuation carries exactly itself, so Segments is never empty.
```

### TextNormalization.fs: normalizeGrouped

Was a doc comment (`///`) at line 99.

```text
Finding 4's contract, in one place, applied identically at authoring time and at scan time,
with the provenance every consumer needs kept alongside the result.

Order matters and is asserted (TextNormalizationTests): NFKC first - it turns some ligatures
and fixed-width forms into their plain equivalents, and it is what turns most non-breaking
space variants into a plain space before foldSpecialSpaces or
collapseRunsOfPlainSpacesAndTabsToOneSpace ever see them - then space folding, then collapse,
then trim, then the within-block join, then drop empties.
Running collapse before NFKC would see untouched non-breaking spaces rather than a collapsible
run of plain ones, and leave them all in the output.

Empty lines are dropped LAST, after the join, which is what stops a blank line between two
lines being read as a wrapped continuation of the first. A dropped line takes its own segment
with it: an empty line neither joins nor is joined to
(currentLooksLikeAWrappedContinuationOfPrevious is false in both directions), so it is always a
group of one.
```

## `MyDogsbody.Domain/InvoiceTemplates/ValidateTemplateWorkflow.fs`

### ValidateTemplateWorkflow.fs: compilePattern

Was a doc comment (`///`) at line 9.

```text
Compiles a user-typed pattern with a match timeout, preferring the NonBacktracking engine and
falling back to the classic backtracking engine - still with the timeout - for constructs
NonBacktracking does not support (lookaround, backreferences). The timeout is the actual
availability guarantee; NonBacktracking is only the cheap way to make it unnecessary for most
patterns.

NonBacktracking rejecting a construct throws NotSupportedException, not ArgumentException -
verified empirically, since a plausible-looking version of this function that caught
ArgumentException instead would let that exception escape uncaught. A malformed pattern throws
RegexParseException (itself an ArgumentException) on either engine. A NULL pattern throws
ArgumentNullException, which neither of those clauses names - and UnvalidatedTemplate is the
untrusted type, so it is guarded ahead of them rather than left to escape as an exception
thrown out of a domain workflow.

Whether the result used the fallback is readable off its own Options - HasFlag
RegexOptions.NonBacktracking - so no separate flag needs to be threaded through this result.
```

### ValidateTemplateWorkflow.fs: validateAsDateFormatByParsingBackTheDateItWritesNotMerelyByFormatting

Was a doc comment (`///`) at line 66.

```text
DateTime.ToString rejects only two things: a lone unknown standard specifier, and an
unterminated quote. Every other unrecognised character is emitted as a literal, so a
format-only check accepts "yyyy-MM-DD" (DD is literal text), "YYYY-MM-DD", "qq" and
"dd/mm/yyyy" (mm is MINUTES) - each of which then silently matches nothing at scan time rather
than reporting anything, the failure mode tasks.md calls the most dangerous one.

Round-tripping the probe and requiring the whole calendar date back rejects all of those: a
format that cannot read back the day, month and year it just wrote cannot read a date off an
invoice either. Requiring the full date (rather than merely that parsing succeeds) is what
catches the literal-text cases - "yyyy-MM-DD" parses its own output happily, but comes back
with the day defaulted.

InvariantCulture at both ends, deliberately - ParseHint's own comment states the rule. Under
ambient culture the probe renders as 2569-03-04 in th-TH and 1447-09-15 in ar-SA, so whether a
template could be saved would otherwise depend on the machine that saved it.
```

### ValidateTemplateWorkflow.fs: ensureEveryDerivationIsDueDateFromIssueDateTheOnlyOneTheEnginePerforms

Was a doc comment (`///`) at line 149.

```text
ApplyTemplateWorkflow evaluates fields in a fixed order and is deliberately not a general
dependency solver, so every other pairing could only ever have yielded None on every message
- a template the user authored, saved without complaint, and which then dropped the field
forever with no error and no field named.

Two that used to save cleanly and extract nothing: DueDate from an AsDate-hinted Reference
(the source is a date, so the checks above pass, but the engine only reads IssueDate), and
IssueDate from DueDate (evaluated first, so the source does not exist yet). requirements.md
asks for a template to be validated "at that moment, not when a scan next runs" - so the
refusal belongs here, where the user is standing in front of the editor.

Runs AFTER ensureEveryDateFromFieldSourceExistsIsADateAndIsNotTheRulesOwnField so that a
self-referencing or missing source keeps reporting the case that names it precisely, rather
than being swallowed by this broader one.
```

### ValidateTemplateWorkflow.fs: ensureHintsMatchFields

Was a doc comment (`///`) at line 179.

```text
A hint the engine cannot use for that field is a template that looks correct and can never
work - the failure the whole validation boundary exists to prevent.

An IssueDate or DueDate rule that READS TEXT must be AsDate: extractDate otherwise defaulted
its format to "" and every message failed the WHOLE extraction with
DateUnparseable(field, raw, ""), an error quoting an empty format string. An Amount rule must
be AsMoney: extractMoney otherwise defaulted its separator to '.' and parsed money anyway out
of a rule the user had hinted as text. Both defaults are unreachable once this is in place,
and both say so in their own doc comments.

A DateFromField rule is exempt. It never parses text, so its hint is not the engine's
business, and requiring one would reject every measured template that derives a due date.
```

### ValidateTemplateWorkflow.fs: reconstructValidTemplate

Was a doc comment (`///`) at line 321.

```text
NOT a second door for untrusted input - the only caller is TemplateRecordMappers.toStoredTemplate,
reading back a row this same function already approved once, when it was originally saved.

design.md does not address how the store builds a ValidTemplate at all: ValidTemplate's
constructor is private to this project, and MyDogsbody.Database is a separate assembly - it
cannot construct one directly even though it references MyDogsbody.Domain. Regex objects also
cannot be persisted to a database column, so every pattern-carrying rule's pattern is
recompiled here from its stored text, deterministically, rather than read back compiled.

This does not re-run the other checks validateTemplate performs (capture group, offset range,
date format, DateFromField soundness) - those already passed once at save time, and re-running
them on every read would mean a row saved under today's rules could become unreadable if a
future change tightened them, turning "read this row" into "read this row if it still
happens to validate".
```

## `MyDogsbody.Domain/Invoices/ApplyTemplateWorkflow.fs`

### ApplyTemplateWorkflow.fs: CandidateDocumentContentPlusTheSubject

Was a doc comment (`///`) at line 11.

```text
The subject is always available, since SubjectCapture reads it regardless of DocumentPart.
Already normalized: it arrives that way on the NormalizedMessage.

Lines are kept GROUPED BY PART rather than flattened into one list. LinesAfterLabel is why:
its offset must not step out of the part the label was found in. content.Lines used to be a
List.collect over every selected part, so a label on the last line of cover-note.pdf with an
offset of 1 returned the first line of the NEXT attachment - a different document whose
BlockIndex numbering is unrelated.

GroupedByPart carries the same text with its provenance - which laid-out lines each joined line
was built from - because LinesAfterLabel counts laid-out lines while every other rule reads
joined ones. See TextNormalization.NormalizedLine for why the two differ.
```

### ApplyTemplateWorkflow.fs: selectEveryCandidateDocumentInMessageOrderNeverFewerThanOne

Was a doc comment (`///`) at line 65.

```text
requirements.md: "WHEN a message carries several attachments THE SYSTEM SHALL apply an
attachment-part template to EACH IN TURN and take the first that yields every required field."
One CandidateDocumentContentPlusTheSubject per matching attachment is what makes that true.
This used to pool every selected part into a single bag and let each rule search the whole of
it independently: tryFindFirstLineCarryingLabelPartByPartWithItsPart took the first PART
carrying the label, runRegexOnEachCandidateUntilOneIsFoundOrTimesOut took the first FILENAME
the pattern matched, and nothing required the two to be the same document. Measured on
a two-attachment message, that returned an Ok invoice whose reference came off 445566.pdf and
whose amount came off cover-letter.pdf - a ledger row that exists in neither of them. A
remittance advice or a covering note attached beside the invoice is all it takes.

WHAT IS ITERATED IS WHAT IS PLURAL. A message has one subject and one body; it can carry any
number of attachments. So the subject and the body stay in scope for every candidate - the
subject always did, which is why SubjectCapture works under any DocumentPart - and the
attachments are taken one at a time. A template reading its reference off a filename and its
amount out of the covering email still works; what it can no longer do is take one field from
one attachment and another field from a different one. That is deliberate rather than a
casualty: an invoice assembled out of two documents is a row that exists in neither.

A selector matching no attachment at all still yields the single no-attachment candidate,
which is what keeps a template of FixedValue and SubjectCapture rules working on a message with
nothing attached.
```

### ApplyTemplateWorkflow.fs: foundUnlessEmpty

Was a doc comment (`///`) at line 123.

```text
requirements.md: "WHEN a rule finds nothing THE SYSTEM SHALL report which field and which rule
found nothing, never a default or an empty value silently substituted."

An extraction that came back empty IS a rule finding nothing, so every outcome is built
through here rather than through Found directly. Three paths used to report Found "": an
AfterLabel on a label-only line (the bare "Reference" line the LinesAfterLabel rules exist
for), a successful match whose capture group did not participate, and a FixedValue of "".
For Reference and Currency that empty value went straight into ExtractedInvoice - and an empty
reference collides in change #4's natural key, turning every such invoice into one ledger row.

The TRIM lives here for the same reason the emptiness check does: it belongs to every outcome,
not to whichever call site remembers it. AfterLabel used to trim its own substring, so
FixedValue and a capture group were the two paths that did not - and Currency is the one field
with no later parse step to trim it, so " AUD " reached ExtractedInvoice verbatim and would
split change #4's natural key against a sibling template's "AUD".
```

### ApplyTemplateWorkflow.fs: LaidOutLineAndTheTextARuleLandingOnItReturns

Was a doc comment (`///`) at line 217.

```text
The two differ exactly where the document hard-wrapped something. A laid-out line that STARTS
a joined group stands for the whole group, so a value the document wrapped comes back whole
rather than as its first physical line; a laid-out line that is a CONTINUATION inside a group
stands only for itself.

Both halves are load-bearing, and they are the two cases the last two review rounds each
found one of. Counting over joined lines made LinesAfterLabel("Reference", 1) answer
differently for "Reference" / "WU-88213" and "Reference" / "wu-88213" - whether a template
worked depended on the case of the first character of a value its author does not control -
which is why the counting moved to laid-out lines. But returning a single laid-out line then
truncated "Description" / "Annual subscription" / "renewal for 2026" to "Annual subscription",
silently, with foundUnlessEmpty unable to tell a truncated value from a complete one. Counting
laid out and returning by group answers both: the continuation "wu-88213" is returned alone
because it starts no group, and the wrapped value is returned whole because it starts one.

Every segment of a group shares one BlockIndex - normalizeGrouped only joins within a block -
so returning a group's joined text can never smuggle in text from the next block past the
boundary check below.
```

### ApplyTemplateWorkflow.fs: parseTheOneNumberOutOfTheTextOrNone

Was a doc comment (`///`) at line 398.

```text
Currency symbols, thousands separators, a trailing CR/DR suffix and a full stop ending the
sentence all fall away; a SECOND number anywhere in the text does not. Two candidates is an
ambiguity this reports rather than resolves - guessing puts a wrong amount in the ledger with
nothing to notice it by, which is the failure requirements.md's "never a default ...
silently substituted" is written against.

A run whose '-' is not in a sign position is not an amount at all, and does not become one by
being the only number-shaped thing on the line - so it falls through to the same refusal two
candidates get rather than to a second, quieter answer.

A pair of brackets around the run is the one piece of decoration that changes the ANSWER
rather than falling away: "(245.00)" is a credit, the same as "245.00 CR" is not.

Known limitation, stated rather than hidden: a document that groups thousands with a SPACE
("1 234,56") reads as two candidates and is refused. That is a reported AmountUnparseable the
user can answer with a RegexCapture rule, not a wrong number - which is what the old filter
produced for the same input.
```

### ApplyTemplateWorkflow.fs: applyTemplate

Was a doc comment (`///`) at line 620.

```text
Applies one template to one message, and hands back the first invoice it can make out of a
SINGLE document. Pure - no I/O, no clock, no randomness, and no dependency parameters;
PaymentTermDays, TemplateId and NormalizedMessage are plain input data, not dependency function
types.

TemplateId is not part of design.md's listed signature for this function, but ExtractedInvoice
and InvoiceError's template-carrying cases both need one and ValidTemplate itself carries
none - a gap in the documented signature, closed here rather than deferred to a caller that
would otherwise have to reconstruct these values after the fact.

The input is a NormalizedMessage rather than a ScannedMessage so that normalization happens
once per message, above selectTemplate's loop, instead of once per candidate template inside
it. Callers reach it through MessageNormalization.normalizeMessage.

This is "first complete match wins" one level below SelectTemplateWorkflow's: that one tries a
supplier's templates in turn, this one tries the message's attachments in turn. The rules do
re-run per attachment, so a message with N attachments costs N rule passes - but normalization,
which is the expensive step, still happens exactly once for the whole message, above both
loops. See selectEveryCandidateDocumentInMessageOrderNeverFewerThanOne for why each
attachment is a candidate of its own.
```

## `MyDogsbody.Domain/Invoices/InvoiceText.fs`

### InvoiceText.fs: normalizeLine

Was a doc comment (`///`) at line 18.

```text
One free-standing string - a mail subject, an attachment filename - put through the same
normalization a document line gets, by making it a one-line, one-block document.

requirements.md: "WHEN any rule is evaluated THE SYSTEM SHALL first apply a defined
normalization to the text, and SHALL apply the identical normalization at authoring time and
at scan time." A subject is text a rule is evaluated against, so "identical" has to mean this
function and not an approximation of it - the authoring test panel shows the user normalized
text, and a pattern written against what the panel showed has to match at scan time.

normalize drops a line that normalizes to nothing, so an empty or whitespace-only input comes
back as "" rather than as a missing element.
```

## `MyDogsbody.Domain/Invoices/InvoicesTypes.fs`

### InvoicesTypes.fs: NormalizedMessage

Was a doc comment (`///`) at line 58.

```text
A ScannedMessage whose text has been through TextNormalization exactly once - every part's
lines, the subject, and every attachment filename.

A distinct stage type rather than a flag or a convention, for the two reasons CLAUDE.md gives
for stage types at all. It makes the normalization impossible to skip: applyTemplate takes one
of these and there is no way to hand it raw text. And it makes the normalization impossible to
repeat: it happens once per message in MessageNormalization, above the loop that tries a
supplier's templates in turn, rather than once per candidate template inside it - NFKC over
every line of every attachment is the most expensive thing in this pipeline, and it used to
run once for each template tried.

Carries no Sender: MatchSupplierWorkflow answers "whose message is this?" from the raw
ScannedMessage before a template is ever chosen, so nothing downstream of normalization needs
one.
```

## `MyDogsbody.Domain/Invoices/MessageNormalization.fs`

### MessageNormalization.fs: normalizeMessage

Was a doc comment (`///`) at line 8.

```text
The subject and the filenames used to be handed to the regex verbatim while only the body
lines were normalized. Mail subjects are one of the likeliest places to carry U+00A0 / U+202F
(mailers insert them around numbers) and NFKC-decomposable full-width forms, so a
SubjectCapture pattern authored against the test panel's normalized display would match there
and silently match nothing at scan time - requirements.md's "WHEN any rule is evaluated" is
explicit that this applies to any rule, not to the document body alone.

Each part's lines are normalized on their own, so the within-block continuation join never
runs across two parts: they are different documents and their BlockIndex numbering is
unrelated.

normalizeGrouped rather than normalize: the grouped form carries which laid-out lines each
joined line was built from, which is what LinesAfterLabel counts its offset over. Producing it
here, in the one pass that already runs once per message, is what keeps the two views of the
same text from ever disagreeing.
```

## `MyDogsbody.Domain/Invoices/ResolveScanWindowWorkflow.fs`

### ResolveScanWindowWorkflow.fs: resolveScanWindow

Was a doc comment (`///`) at line 7.

```text
Given the windows the store holds and the day count the user last chose (a NUMBER, not a
foreign key - it survives its row being deleted), return the window to open on:

  remembered is present in the store      -> that one
  remembered is absent (or None)          -> 14, if 14 is present
  remembered is absent and 14 is absent   -> the shortest window still present
  the store holds nothing (cannot happen  -> the fallback constant, constructed
    - CannotDeleteLastScanWindow forbids it)

The remembered-but-since-deleted row is the case nobody tries by hand; it has its own test.
```

## `MyDogsbody.Domain/Invoices/ScanForInvoicesWorkflow.fs`

### ScanForInvoicesWorkflow.fs: orAttachmentCause

Was a comment (`//`) at line 72.

```text
An attachment that could not be read, or whose format has no reader, is the more useful
diagnostic whenever the message yielded nothing: it is a fact ABOUT THE MESSAGE, whereas
every other cause here is a conclusion about the template, and it is recorded nowhere
else - ScanMessageWorkflow hands it over exactly once, in this list. Reported only for a
message that produced no invoice, since a scan records one problem per message and a
message that yielded an invoice has its rows cleared by clearScanProblems.

requirements.md names these two among the eight distinguishable causes, asks that an
unsupported format be named "so the question of whether to build a reader for it can
later be answered from data", says a legacy .doc "SHALL NOT" be skipped silently, and
pins the case outright: "WHEN an attachment is empty or zero bytes THE SYSTEM SHALL
report it as unreadable RATHER THAN AS TEXT THAT MATCHED NOTHING."

Consulting the list only when NO supplier matched left both unreachable for a CONFIGURED
supplier - the case the feature exists for - and produced two measured wrong diagnostics:

  RuleFoundNothing(acme, acme-t1, "Reference")  - literally the sentence the requirement
                                                  above forbids, for an unreadable PDF;
  NoTemplateMatched(acme)                       - for a supplier that HAS a PDF template.

The second is the worse of the two and is why this covers the whole selectTemplate
branch: SelectTemplateWorkflow filters out every template whose DocumentPart the message
does not carry, and an attachment that failed to read is not among the message's parts.
So the one supplier configured correctly is told to go and configure a template that is
already there. outcome.md's 12.5 run recorded NoTemplateMatched twice against the real
mailbox.
```

### ScanForInvoicesWorkflow.fs: resettingWatermarksOnError

Was a doc comment (`///`) at line 131.

```text
Everything after `readMailFolder` runs with every folder's watermark already advanced to EOF -
`MailFolderReader.readFolder` saves it as part of reading, before a single message is processed.
So ANY abort from there on strands the mail this scan read behind an "already read" mark:
`resumeOffset` resumes from `OffsetReached` whenever the file has only grown, so the next scan
answers "nothing new" for messages that never became an invoice or a problem. No invoice, no
problem, nothing on screen (design.md -> Decisions taken #17; requirements.md -> "SHALL NOT
advance them past mail it read but never turned into an invoice or a problem").

The ORIGINAL error is returned whether or not the clear succeeded, so a broken store - the usual
cause of an abort here - does not mask itself behind a second failure.
```

## `MyDogsbody.Domain/Invoices/SelectTemplateWorkflow.fs`

### SelectTemplateWorkflow.fs: selectTemplate

Was a doc comment (`///`) at line 32.

```text
supplierId is not part of design.md's listed signature, but NoTemplateForSupplier needs one
and an empty template list would otherwise carry none to report - the same gap
ApplyTemplateWorkflow's missing TemplateId was.

Two things this deliberately does not delegate:

The SORT is this workflow's, not the store's. design.md's diagram for it opens with
"templates for supplier, in stored Position order" and ValidTemplate carries Position' for
exactly this. A LoadTemplatesForSupplier adapter returning rows in insertion or rowid order
would otherwise silently override everything ReorderTemplatesWorkflow exists to let the user
configure - and here the order decides WHICH TEMPLATE WINS, not merely what a page displays,
so the failure is a wrong-but-plausible invoice from the wrong template. List.sortBy is
stable, so templates sharing a position keep the order the dependency returned them in.

The SUPPLIER is reconciled rather than assumed. ExtractedInvoice.SupplierId comes from the
template, so a caller pairing one supplier's id with another supplier's template list used to
get an Ok invoice filed under the template's supplier, silently. Templates belonging to anyone
else are dropped here, which makes the parameter load-bearing on the success path instead of
only in the error case.
```

## `MyDogsbody.Domain/MailAccounts/ScanForMailAccountsWorkflow.fs`

### ScanForMailAccountsWorkflow.fs: carryTheCachedMessageCountForwardByAccountIdFromThePreviouslyStoredAccounts

Was a doc comment (`///`) at line 32.

```text
A message count is a separate, user-triggered action whose header pass costs *minutes* on the
measured profile (design.md -> Decisions taken #4), which is the whole reason it is cached with
the time it was taken rather than computed on render. A scan re-reads prefs.js and re-enumerates
folders; it never counts messages, so discovery reports every account with no count at all.
Storing that straight over the previous set threw the figure away for an account the scan had
just found again - and requirements.md's "state when it was taken, because the count is a
snapshot and the mailbox keeps growing" only means anything if a stale count survives to be
stated.

Everything else about the row is the fresh scan's answer, so this cannot turn into merging a
stale row forward. An account the scan no longer finds is not resurrected - a fresh scan is
still the whole truth about WHICH accounts exist. A discovery that somehow arrived with a
count of its own keeps it.
```

## `MyDogsbody.Domain/Result.fs`

### Result.fs: ResultBuilder

Was a doc comment (`///`) at line 3.

```text
The domain's own Result computation expression.

This exists because MyDogsbody.Builders.HandleErrorBuilder cannot serve the centre: its
Bind returns Result&lt;_, MyDogsbodyException&gt; and its TryWith handler returns one, so its
error type is pinned rather than generic - it could never bind a Result&lt;_, SupplierError&gt;.
It also lives in a project the domain is not allowed to reference.

So this is that builder with two things taken out: the writeLog constructor parameter, and
the annotations that pin the error type. There is deliberately no TryWith - the domain never
catches exceptions, because it never performs the I/O that raises them. An expected failure
here is a discriminated union case and was never an exception in the first place.
```
