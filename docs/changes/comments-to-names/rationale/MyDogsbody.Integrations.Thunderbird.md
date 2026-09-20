# Rationale moved out of `MyDogsbody.Integrations.Thunderbird`

Comment blocks of 10 lines or more, moved here verbatim by the
`comments-to-names` change (phase 10). Each was replaced in the source by one line naming its
section below. Nothing was reworded; the text is exactly what the source held.

## `MyDogsbody.Integrations.Thunderbird/MailFolderReader.fs`

### MailFolderReader.fs: headerBlockAndTheTextAfterTheBlankLineThatEndsItOrNoneWhenThereIsNoBlankLine

Was a doc comment (`///`) at line 60.

```text
For the LAST message in a file, no blank line at all means it is torn: Thunderbird was still
writing it.

BOTH line endings are recognised, and that is load-bearing rather than defensive. RFC 5322
mandates CRLF, and a message written to the store exactly as it arrived over IMAP or SMTP
keeps it, so a folder can hold CRLF messages whatever the file's own convention is. Looking
only for "\n\n" made every such message report "no separator", which `classifySegment` turns
into a silently dropped message and a torn-message offset - data loss with nothing on screen
to show for it. Whichever separator appears first wins, so a CRLF header block followed by an
LF blank line inside the body still splits at the header block.
```

### MailFolderReader.fs: resumeOffset

Was a doc comment (`///`) at line 129.

```text
Where the next read of this folder should begin: the watermark's offset when resuming from it
is still sound, or 0 for a full re-read. Pure and public so every branch is unit-tested without
a file, the same reason `normalizeStartOffset` is.

A resume needs BOTH halves to hold. Size and modification time say the bytes already passed are
still the same bytes. `CutoffReached` says they were examined for a window no wider than the
one being asked about now - and that is the half the size/mtime pair cannot supply, because
picking a longer scan window changes neither. `classifySegment` skips a message older than the
cutoff BEFORE parsing its body, so those messages were passed over, not read; a later, wider
cutoff is asking for exactly them. Resuming would answer "nothing new" for mail that was never
looked at - the user widens 7 days to 180, presses "Scan now", and gets an empty result with
nothing on screen to say why.

The recorded cutoff is the LAST one used, not the earliest ever used, and deliberately: after
a narrow scan resumed from a wide one, the bytes appended since were examined only at the
narrow cutoff, so the widest-ever claim would be false for them. Keeping the last one costs a
re-read when a window is narrowed and then widened again, and that is the conservative side to
err on - the upsert is on the natural key, so a re-read updates rather than duplicates.

A cutoff that slides forward by a day because the same N-day window is scanned again tomorrow
is not a widening: it is later than the recorded one, so the resume stands.
```

### MailFolderReader.fs: segmentStartOffsets

Was a doc comment (`///`) at line 171.

```text
The byte offsets within `bytes` at which a message segment begins: every "From " line whose
predecessor line is blank - one immediately preceded by "\n\n" or "\n\r\n" - plus offset 0
when `bytes` itself starts with a "From " line.

Byte-scanned rather than string-split (the old `splitIntoMessages`), so a segment larger than
a .NET string is still located rather than throwing on the way in. A properly mbox-quoted
">From " never matches; an unquoted "From " opening a body line preceded by a blank one does,
exactly as before - `tryParseMessage` is what holds the resulting fragment. The blank line has
to be VISIBLE in `bytes`: a "From " at offset 0 or 1 whose preceding newline was left in the
previous buffer is `foldMboxSegments`'s concern, not this function's.
```

### MailFolderReader.fs: | :? MimePart as part when not (isNull part.Content) -> Some

Was a comment (`//`) at line 364.

```text
A part whose headers are on disk but whose body is not has a NULL `Content`, and
that is the ordinary state of an mbox while Thunderbird is flushing a message
with an attachment: the part's headers go down before its base64 does. The
message's own headers and their blank line are already written by then, so
`readMboxFile`'s torn test (no header/body separator) passes it as complete and
it reaches this line - where `toMailAttachment` used to dereference the null and
throw a `NullReferenceException`. That is not a `FormatException`, so
`tryParseMessage` did not hold it, and not an `IOException` or an
`UnauthorizedAccessException`, so neither `readMboxFile` nor `readMaildirFolder`
did either: it escaped `readFolder`, `read` and the whole API call, from
signatures that say `Result<_, MailAccountError>`. Truncating one realistic
invoice email at each of its 666 byte offsets reaches this state at 64 of them.

Skipped rather than reported as an empty attachment: there are no bytes behind
it, so handing the invoice pipeline a zero-byte "invoice.pdf" would turn "not
written yet" into "this PDF is corrupt". The message around it is entirely
readable and is still returned - the same instinct as the torn-message rule,
which keeps everything before the tear.
```

### MailFolderReader.fs: tryParseMessage

Was a doc comment (`///`) at line 387.

```text
MimeKit raises `FormatException` for content it cannot parse as a message at all - "Failed to
parse message headers." - and that is neither an `IOException` nor an
`UnauthorizedAccessException`, so it escaped `readMboxFile`'s and `readMaildirFolder`'s
handlers, out of `readFolder`, out of `read`, and out of the whole API call. A `Result`-
returning reader must not throw, and requirements.md -> "Reading safely while Thunderbird is
running" asks for the other folders to keep returning.

This is reachable from ordinary mail, not only from a corrupt file. mbox carries no length
header, so `segmentStartOffsets` has to guess a boundary from an unquoted "From " at the start
of a line preceded by a blank one - which a plain-text body signing off "From the accounts
team," satisfies exactly. The half after that false boundary begins with body text where
RFC822 headers should be, and one such line anywhere in one folder took down every folder of
the account. (A properly mbox-quoted ">From " never produces the split - FromQuotedBody.mbox
covers that - but nothing makes a sender quote it.)

Discarded rather than reported as a folder-level failure, and deliberately: the fragment is
the tail of a message that IS returned, so dropping it loses a body's last lines, whereas
failing the folder would lose every message in it - on a real INBOX, all of them, on every
scan. Same treatment a torn final message gets (requirements.md: "discard that partial message
and return everything before it, rather than failing the folder"), except that the offset still
advances past it: unlike a torn message, this will never become parseable.
```

### MailFolderReader.fs: accountWithReadableStore

Was a doc comment (`///`) at line 562.

```text
Discovery deliberately KEEPS a configured-but-missing account rather than dropping it
(requirements.md -> "Reading the profile"), and enumeration gives it no folders, because there
is no directory to enumerate. Both readers below then fold over an empty folder list and answer
"nothing here" - `Ok 0` and `Ok []` - which is exactly what a real, empty account answers. The
user asked how many messages their account holds, was told none, and nothing said the store had
gone: a silent wrong result, and the one this area's own error DU has carried the case for
(`StoreDirectoryMissing`) since the first commit without anything ever raising it.

Checked live rather than read off the account's `StoreDirectoryExists` flag: that flag is a
snapshot from the last scan, and the likely case is a drive unplugged or a folder deleted
BETWEEN scanning and counting, which a snapshot cannot see. The error says "does not exist" in
the present tense, so the present is what it has to be checked against.
```

## `MyDogsbody.Integrations.Thunderbird/ThunderbirdEntityMappers.fs`

### ThunderbirdEntityMappers.fs: toNewWatermarkEntity

Was a doc comment (`///`) at line 112.

```text
`ModifiedAt` is deliberately persisted as UTC ticks rather than as a DateTime column - see
ScanWatermarkEntity for what LiteDB does to a DateTime, and why readFolder's equality check
cannot survive it. This is a rename, not a new field: `ModifiedAt` became
`ModifiedAtTicksUtc`.

`CutoffReached` is persisted as ticks for the same reason, and reconstructed as
`DateTimeKind.Unspecified` rather than Utc: the cutoff comes from `GetCurrentTime`, which the
composition root binds to `DateTime.Now`, so it is a local-clock value and calling it UTC would
be a lie. Only `<` is ever applied to it, and DateTime comparison is on ticks regardless of
Kind. Ticks of 0 (`DateTime.MinValue`) is what an entity stored before this field existed
decodes to, and `resumeOffset` reads that as "not recorded".
```
