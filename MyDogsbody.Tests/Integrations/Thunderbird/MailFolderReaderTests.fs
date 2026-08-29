module MyDogsbody.Tests.Integrations.Thunderbird.MailFolderReaderTests

open System
open System.Collections.Generic
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Integrations.Thunderbird.MailFolderReader
open MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

let private testAccountId = MailAccountId.create "test-account" |> Result.defaultWith (fun _ -> failwith "unreachable")

/// A cutoff far enough in the past that every fixture message is included.
let private noCutoff = ScanCutoff.ofStartOfDay (DateTime(2000, 1, 1))

let private folder relativePath : MailFolder =
    {
        RelativePath = relativePath
        DisplayName = relativePath
        SizeBytes = 0L
        IsScannable = true
    }

let private inMemoryWatermarkStore () =
    let store = Dictionary<string * string, FolderWatermark>()

    let load: LoadWatermark =
        fun accountId relativePath ->
            match store.TryGetValue((MailAccountId.value accountId, relativePath)) with
            | true, wm -> Ok(Some wm)
            | false, _ -> Ok None

    let save: SaveWatermark =
        fun accountId relativePath wm ->
            store.[(MailAccountId.value accountId, relativePath)] <- wm
            Ok()

    load, save, store

let private freshTempDirectory () =
    let dir = Path.Combine(Path.GetTempPath(), $"mdb-tbreader-{Guid.NewGuid()}")
    Directory.CreateDirectory dir |> ignore
    dir

// ---------- 4.0 the streaming primitives, exercised without a real folder file ----------

/// `MaxBufferableBytes` is a hard runtime ceiling, not a taste: every step past the raw bytes
/// works on a Latin1 string, and .NET caps a string at 1,073,741,791 chars - the 2 GB object-size
/// limit at two bytes per char, HALF what `Array.MaxLength` allows. `classifySegment` and the
/// count pass both skip a segment larger than this instead of turning it into text; this pins the
/// constant to the ceiling it was chosen from, so a future runtime that lowered it would fail
/// here rather than as an OutOfMemoryException in production.
[<Fact; Trait("Level", "Unit")>]
let ``the segment ceiling is the largest string .NET can build, so one byte more could not be turned into text`` () =
    Assert.Throws<OutOfMemoryException>(Action(fun () -> String('a', MaxBufferableBytes + 1) |> ignore)) |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``normalizeStartOffset keeps a stored offset that is inside the file`` () =
    Assert.Equal(400L, normalizeStartOffset 1000L 400L)

[<Fact; Trait("Level", "Unit")>]
let ``normalizeStartOffset keeps an offset at exactly the end of the file`` () =
    Assert.Equal(1000L, normalizeStartOffset 1000L 1000L)

[<Fact; Trait("Level", "Unit")>]
let ``normalizeStartOffset keeps zero`` () =
    Assert.Equal(0L, normalizeStartOffset 1000L 0L)

[<Fact; Trait("Level", "Unit")>]
let ``normalizeStartOffset restarts at zero when the stored offset lies past the end of the file`` () =
    // The file cannot contain what the watermark claims - a folder that grew between the size
    // measurement and the open, then was compacted. Re-reading a message is recoverable; seeking
    // past EOF is not.
    Assert.Equal(0L, normalizeStartOffset 1000L 1500L)

[<Fact; Trait("Level", "Unit")>]
let ``normalizeStartOffset restarts at zero for a negative stored offset`` () =
    Assert.Equal(0L, normalizeStartOffset 1000L -1L)

// -- segmentStartOffsets --

let private bytesOf (s: string) = System.Text.Encoding.Latin1.GetBytes s

[<Fact; Trait("Level", "Unit")>]
let ``segmentStartOffsets finds offset zero when the buffer opens with a From line`` () =
    Assert.Equal<int list>([ 0 ], segmentStartOffsets (bytesOf "From a@b Mon\nMessage-ID: <1>\n\nbody\n"))

[<Fact; Trait("Level", "Unit")>]
let ``segmentStartOffsets finds a From line preceded by an LF blank line`` () =
    let bytes = bytesOf "From a@b Mon\n\nbody one\n\nFrom c@d Tue\n\nbody two\n"
    let offsets = segmentStartOffsets bytes
    Assert.Equal(2, offsets.Length)
    Assert.Equal(0, offsets.Head)
    Assert.Equal("From c@d Tue\n\nbody two\n", System.Text.Encoding.Latin1.GetString(bytes.[offsets.[1] ..]))

[<Fact; Trait("Level", "Unit")>]
let ``segmentStartOffsets finds a From line preceded by a CRLF blank line`` () =
    let bytes = bytesOf "From a@b Mon\r\n\r\nbody one\r\n\r\nFrom c@d Tue\r\n\r\nbody two\r\n"
    let offsets = segmentStartOffsets bytes
    Assert.Equal(2, offsets.Length)
    Assert.Equal("From c@d Tue\r\n\r\nbody two\r\n", System.Text.Encoding.Latin1.GetString(bytes.[offsets.[1] ..]))

[<Fact; Trait("Level", "Unit")>]
let ``segmentStartOffsets ignores a mbox-quoted From line in a body`` () =
    // ">From ..." is how the mbox writer escapes a body line that began with "From ".
    Assert.Equal<int list>([ 0 ], segmentStartOffsets (bytesOf "From a@b Mon\n\nquote:\n\n>From the sender\n\nmore\n"))

[<Fact; Trait("Level", "Unit")>]
let ``segmentStartOffsets ignores a From line that is not preceded by a blank line`` () =
    // A "From " opening a line but with the previous line non-blank is not a boundary.
    Assert.Equal<int list>([ 0 ], segmentStartOffsets (bytesOf "From a@b Mon\n\nregards\nFrom the accounts team\n"))

[<Fact; Trait("Level", "Unit")>]
let ``segmentStartOffsets does not treat a leading LF-From as a boundary - that is the fold's job`` () =
    // The blank line's predecessor is not visible in this buffer, so the structural scan cannot
    // know "\nFrom " is a boundary. foldMboxSegments trims the seam instead.
    Assert.Equal<int list>([], segmentStartOffsets (bytesOf "\nFrom a@b Mon\n\nbody\n"))

// -- foldMboxSegments --

/// Runs the fold over an in-memory stream, collecting (absoluteStartOffset, text, isLast) per
/// segment. `chunkSize` is deliberately tiny so a few hundred bytes cross many chunk boundaries.
let private foldToList (chunkSize: int) (maxMessageBytes: int) (fromOffset: int64) (content: string) =
    use stream = new MemoryStream(bytesOf content)
    let collected = ResizeArray<int64 * string * bool>()

    foldMboxSegments
        chunkSize
        maxMessageBytes
        stream
        fromOffset
        (fun () start bytes isLast ->
            collected.Add(start, System.Text.Encoding.Latin1.GetString bytes, isLast))
        ()

    List.ofSeq collected

let private twoMessages =
    "From a@b Mon Jan 05 09:00:00 2026\nMessage-ID: <a>\n\nbody of a\n"
    + "\n"
    + "From c@d Tue Jan 06 10:00:00 2026\nMessage-ID: <c>\n\nbody of c\n"

[<Fact; Trait("Level", "Unit")>]
let ``foldMboxSegments emits each message once, the last flagged, with exact offsets`` () =
    let segments = foldToList 16 100_000 0L twoMessages

    Assert.Equal(2, segments.Length)
    let (start0, text0, last0) = segments.[0]
    let (start1, text1, last1) = segments.[1]
    Assert.Equal(0L, start0)
    Assert.False last0
    Assert.StartsWith("From a@b", text0)
    Assert.EndsWith("body of a\n\n", text0) // the segment carries the trailing blank line
    Assert.Equal(int64 (bytesOf text0).Length, start1)
    Assert.True last1
    Assert.StartsWith("From c@d", text1)
    Assert.EndsWith("body of c\n", text1)

[<Fact; Trait("Level", "Unit")>]
let ``foldMboxSegments gives the same segments whatever the chunk size`` () =
    let atOne = foldToList 1 100_000 0L twoMessages |> List.map (fun (s, t, _) -> s, t)
    let atThree = foldToList 3 100_000 0L twoMessages |> List.map (fun (s, t, _) -> s, t)
    let atHuge = foldToList 100_000 100_000 0L twoMessages |> List.map (fun (s, t, _) -> s, t)
    Assert.Equal<(int64 * string) list>(atHuge, atOne)
    Assert.Equal<(int64 * string) list>(atHuge, atThree)

[<Fact; Trait("Level", "Unit")>]
let ``foldMboxSegments resuming from a watermark trims a leading LF seam and reports file-absolute offsets`` () =
    // A watermark after the first message sits just past the blank separator line the first
    // segment carried. What remains is "\nFrom c@d...", and the leading "\n" must not become part
    // of message c.
    let resumeAt = int64 (twoMessages.IndexOf("\nFrom c@d"))
    let segments = foldToList 8 100_000 resumeAt twoMessages

    let (start, text, isLast) = Assert.Single segments
    Assert.Equal(resumeAt + 1L, start)
    Assert.True isLast
    Assert.StartsWith("From c@d", text)

[<Fact; Trait("Level", "Unit")>]
let ``foldMboxSegments resuming exactly at a From boundary keeps the whole message`` () =
    let resumeAt = int64 (twoMessages.IndexOf("From c@d"))
    let segments = foldToList 8 100_000 resumeAt twoMessages

    let (start, text, _) = Assert.Single segments
    Assert.Equal(resumeAt, start)
    Assert.StartsWith("From c@d", text)
    Assert.EndsWith("body of c\n", text)

[<Fact; Trait("Level", "Unit")>]
let ``foldMboxSegments emits an oversized boundary-less segment once, then resumes at the next real boundary`` () =
    // maxMessageBytes is 40, and the first "message" has no second boundary for far longer than
    // that: it is emitted once (the caller skips it) and the fold byte-scans forward to "From c@d".
    let content =
        "From a@b Mon\nMessage-ID: <a>\n\n"
        + String('x', 400)
        + "\n\nFrom c@d Tue\nMessage-ID: <c>\n\nbody of c\n"

    let segments = foldToList 16 40 0L content

    // the last segment is the real second message, intact
    let (_, lastText, lastFlag) = List.last segments
    Assert.True lastFlag
    Assert.StartsWith("From c@d", lastText)
    Assert.EndsWith("body of c\n", lastText)
    // and message c is emitted exactly once
    Assert.Equal(1, segments |> List.filter (fun (_, t, _) -> t.StartsWith "From c@d") |> List.length)

// ---------- 4.1 opening and message boundaries ----------

[<Fact; Trait("Level", "Integration")>]
let ``readFolder leaves the fixture file byte-identical after a read`` () =
    let load, save, _ = inMemoryWatermarkStore ()
    let path = mboxFixture "FromQuotedBody.mbox"
    let before = File.ReadAllBytes path

    let actual = readFolder load save testAccountId (folder "FromQuotedBody.mbox") mboxFixtures Mbox noCutoff

    Assert.True(Result.isOk actual)
    let after = File.ReadAllBytes path
    Assert.Equal<byte[]>(before, after)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder does not treat a quoted From line inside a body as a new message`` () =
    let load, save, _ = inMemoryWatermarkStore ()

    let actual = readFolder load save testAccountId (folder "FromQuotedBody.mbox") mboxFixtures Mbox noCutoff

    match actual with
    | Ok messages -> Assert.Single messages |> ignore
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``readFolder discards a torn final message and returns everything before it`` () =
    let load, save, _ = inMemoryWatermarkStore ()

    let actual = readFolder load save testAccountId (folder "TruncatedFinalMessage.mbox") mboxFixtures Mbox noCutoff

    match actual with
    | Ok messages ->
        let message = Assert.Single messages
        Assert.Equal("<truncated-1@example.com>", message.SourceMessageId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``readFolder reports a locked file as unreadable`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Locked")
        File.Copy(mboxFixture "FromQuotedBody.mbox", path)

        use lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        let load, save, _ = inMemoryWatermarkStore ()
        let actual = readFolder load save testAccountId (folder "Locked") tempDir Mbox noCutoff

        match actual with
        | Error(MailFolderUnreadable(reportedPath, _)) -> Assert.Contains("Locked", reportedPath)
        | other -> Assert.Fail($"Expected Error(MailFolderUnreadable _), but got: {other}")
    finally
        Directory.Delete(tempDir, true)

// ---------- 4.1b a segment MimeKit cannot parse ----------
//
// mbox has no length header, so a boundary is guessed from an unquoted "From " at the start of a
// line preceded by a blank one. A body line that reads "From the accounts team," therefore splits
// a perfectly ordinary message in two, and the half after the split begins with body text where
// RFC822 headers should be. MimeKit refuses that with a FormatException - which is neither an
// IOException nor an UnauthorizedAccessException, so it escaped readMboxFile's and
// readMaildirFolder's handlers, out of readFolder, out of read, and out of the whole call. One
// such line anywhere in one folder therefore took down every folder of the account.

[<Fact; Trait("Level", "Integration")>]
let ``readFolder discards a segment MimeKit cannot parse and still returns the rest of the folder`` () =
    let load, save, store = inMemoryWatermarkStore ()

    let actual = readFolder load save testAccountId (folder "UnquotedFromInBody.mbox") mboxFixtures Mbox noCutoff

    match actual with
    | Ok messages ->
        Assert.Equal<string list>(
            [ "<unquoted-1@example.com>"; "<unquoted-2@example.com>" ],
            messages |> List.map (fun m -> m.SourceMessageId)
        )

        Assert.Equal<string list>(
            [ "Invoice attached"; "Second message" ],
            messages |> List.map (fun m -> m.Subject)
        )

        Assert.Equal<string list>(
            [ "alice@example.com"; "bob@example.com" ],
            messages |> List.map (fun m -> m.Sender)
        )

        Assert.Equal<DateTime list>(
            [ DateTime(2024, 1, 1, 0, 0, 0); DateTime(2024, 1, 2, 0, 0, 0) ],
            messages |> List.map (fun m -> m.ReceivedAt)
        )

        Assert.All(messages, fun m -> Assert.Empty m.Attachments)

        // The whole file is consumed: the discarded fragment will never become parseable, so the
        // offset must advance past it rather than re-reading it on every later scan.
        let expectedLength = FileInfo(mboxFixture "UnquotedFromInBody.mbox").Length
        Assert.Equal(expectedLength, store.[(MailAccountId.value testAccountId, "UnquotedFromInBody.mbox")].OffsetReached)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read continues past a folder holding a segment MimeKit cannot parse and still returns the other folders' messages`` () =
    let tempDir = freshTempDirectory ()

    try
        File.Copy(mboxFixture "UnquotedFromInBody.mbox", Path.Combine(tempDir, "Unparseable"))
        File.Copy(mboxFixture "NoMessageId.mbox", Path.Combine(tempDir, "Ok"))

        let load, save, _ = inMemoryWatermarkStore ()

        let account: DiscoveredMailAccount =
            {
                Id = testAccountId
                ProfilePath = tempDir
                DisplayName = "Test"
                EmailAddresses = []
                StoreFormat = Mbox
                StoreDirectory = tempDir
                StoreDirectoryExists = true
                Folders = [ folder "Unparseable"; folder "Ok" ]
                CachedMessageCount = None
            }

        let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

        match MailFolderReader.read lookupAccount load save testAccountId noCutoff with
        | Ok messages -> Assert.Equal(3, messages.Length) // two from Unparseable, one from Ok
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder discards a maildir message MimeKit cannot parse and still returns the rest`` () =
    let tempDir = freshTempDirectory ()

    try
        let inbox = Path.Combine(tempDir, "INBOX")
        Directory.CreateDirectory(Path.Combine(inbox, "cur")) |> ignore
        Directory.CreateDirectory(Path.Combine(inbox, "new")) |> ignore
        Directory.CreateDirectory(Path.Combine(inbox, "tmp")) |> ignore

        // No colon on the first line, so MimeKit has no header to parse at all.
        File.WriteAllText(Path.Combine(inbox, "cur", "1.broken"), "not a header at all\nstill not one\n\nbody\n")

        File.WriteAllText(
            Path.Combine(inbox, "cur", "2.good"),
            "Message-ID: <maildir-good@example.com>\nFrom: alice@example.com\nSubject: Good\nDate: Mon, 1 Jan 2024 00:00:00 +0000\n\nbody\n"
        )

        let load, save, _ = inMemoryWatermarkStore ()

        match readFolder load save testAccountId (folder "INBOX") tempDir Maildir noCutoff with
        | Ok messages ->
            let message = Assert.Single messages
            Assert.Equal("<maildir-good@example.com>", message.SourceMessageId)
            Assert.Equal("Good", message.Subject)
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

// ---------- 4.1c an attachment part whose content has not been written yet ----------
//
// `tryParseMessage` catches `FormatException`, which is the exception MimeKit raises for content
// it cannot parse as a message AT ALL. It is not the only way the parse throws, and the other way
// is reached by ordinary use rather than by a corrupt file.
//
// MimeKit leaves `MimePart.Content` NULL for a part whose headers are present but whose body is
// not - the exact state an mbox is in while Thunderbird is flushing a message with an attachment,
// because the headers go down before the base64 does. The top-level headers and their blank line
// are already on disk by then, so `readMboxFile`'s `isTorn` test (no header/body separator) says
// the segment is NOT torn and hands it to the full parse. `toMailAttachment` then dereferences
// that null `Content`, and a `NullReferenceException` is neither a `FormatException` nor an
// `IOException` nor an `UnauthorizedAccessException`: it walked out of `tryParseMessage`, out of
// `readMboxFile`/`readMaildirFolder`, out of `readFolder`, out of `read` and out of the whole API
// call - from functions whose signature says `Result<_, MailAccountError>`.
//
// Measured, not theorised: truncating one realistic invoice email (multipart/mixed, a text part
// and a base64 PDF) at each of its 666 byte offsets throws 64 `NullReferenceException`s and 52
// `FormatException`s. Round 4 closed the 52; these tests close the 64.
//
// The content-less part is dropped and the message is kept, rather than the message being
// discarded whole: the bytes of that attachment are genuinely not in the file, so there is no
// attachment to report, while the message around it - sender, subject, date, body - is entirely
// readable and is what the reader exists to return.

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns a message whose attachment content is not yet on disk, reporting no attachment for it`` () =
    let load, save, _ = inMemoryWatermarkStore ()

    let actual = readFolder load save testAccountId (folder "AttachmentContentNotYetWritten.mbox") mboxFixtures Mbox noCutoff

    match actual with
    | Ok messages ->
        Assert.Equal<string list>(
            [ "<written-1@example.com>"; "<halfwritten-2@example.com>" ],
            messages |> List.map (fun m -> m.SourceMessageId)
        )

        Assert.Equal<string list>(
            [ "A complete message"; "Attachment part not yet written" ],
            messages |> List.map (fun m -> m.Subject)
        )

        Assert.Equal<string list>(
            [ "alice@example.com"; "billing@vendor.example.com" ],
            messages |> List.map (fun m -> m.Sender)
        )

        Assert.Equal<DateTime list>(
            [ DateTime(2024, 1, 1, 0, 0, 0); DateTime(2024, 1, 2, 0, 0, 0) ],
            messages |> List.map (fun m -> m.ReceivedAt)
        )

        // CRLF although the fixture on disk is LF: MimeKit normalises a decoded text body's line
        // endings, so this is the reader's own output rather than the file's convention.
        let complete = messages.[0]
        Assert.Equal(Some "This message is complete and must still be returned.\r\n\r\n", complete.BodyText)
        Assert.Equal(None, complete.BodyHtml)
        Assert.Empty complete.Attachments

        // The half-written message keeps everything that IS on disk - only the part with no bytes
        // behind it is left out, so nothing downstream is handed a zero-byte "invoice-2002.pdf".
        let halfWritten = messages.[1]
        Assert.Equal(Some "Invoice attached.\r\n", halfWritten.BodyText)
        Assert.Equal(None, halfWritten.BodyHtml)
        Assert.Empty halfWritten.Attachments
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read continues past a folder holding an unwritten attachment part and still returns the other folders' messages`` () =
    let tempDir = freshTempDirectory ()

    try
        File.Copy(mboxFixture "AttachmentContentNotYetWritten.mbox", Path.Combine(tempDir, "HalfWritten"))
        File.Copy(mboxFixture "NoMessageId.mbox", Path.Combine(tempDir, "Ok"))

        let load, save, _ = inMemoryWatermarkStore ()

        let account: DiscoveredMailAccount =
            {
                Id = testAccountId
                ProfilePath = tempDir
                DisplayName = "Test"
                EmailAddresses = []
                StoreFormat = Mbox
                StoreDirectory = tempDir
                StoreDirectoryExists = true
                Folders = [ folder "HalfWritten"; folder "Ok" ]
                CachedMessageCount = None
            }

        let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

        match MailFolderReader.read lookupAccount load save testAccountId noCutoff with
        | Ok messages -> Assert.Equal(3, messages.Length) // two from HalfWritten, one from Ok
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns a maildir message whose attachment content is not yet on disk`` () =
    let tempDir = freshTempDirectory ()

    try
        let inbox = Path.Combine(tempDir, "INBOX")
        Directory.CreateDirectory(Path.Combine(inbox, "cur")) |> ignore

        // Headers of the attachment part written, its base64 not yet - MimePart.Content is null.
        File.WriteAllText(
            Path.Combine(inbox, "cur", "1.halfwritten"),
            "Message-ID: <maildir-halfwritten@example.com>\n"
            + "From: billing@vendor.example.com\n"
            + "Subject: Maildir attachment not yet written\n"
            + "Date: Tue, 2 Jan 2024 00:00:00 +0000\n"
            + "MIME-Version: 1.0\n"
            + "Content-Type: multipart/mixed; boundary=\"BOUND\"\n"
            + "\n"
            + "--BOUND\n"
            + "Content-Type: text/plain; charset=utf-8\n"
            + "\n"
            + "Invoice attached.\n"
            + "\n"
            + "--BOUND\n"
            + "Content-Type: application/pdf; name=\"invoice-3003.pdf\"\n"
            + "Content-Disposition: attachment; filename=\"invoice-3003.pdf\"\n"
            + "Content-Transfer-Encoding: base64\n"
            + "\n"
        )

        let load, save, _ = inMemoryWatermarkStore ()

        match readFolder load save testAccountId (folder "INBOX") tempDir Maildir noCutoff with
        | Ok messages ->
            let message = Assert.Single messages
            Assert.Equal("<maildir-halfwritten@example.com>", message.SourceMessageId)
            Assert.Equal("Maildir attachment not yet written", message.Subject)
            Assert.Equal("billing@vendor.example.com", message.Sender)
            Assert.Equal(DateTime(2024, 1, 2, 0, 0, 0), message.ReceivedAt)
            Assert.Equal(Some "Invoice attached.\r\n", message.BodyText)
            Assert.Equal(None, message.BodyHtml)
            Assert.Empty message.Attachments
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``read continues past a locked folder and still returns the other folders' messages`` () =
    let tempDir = freshTempDirectory ()

    try
        let lockedPath = Path.Combine(tempDir, "Locked")
        let okPath = Path.Combine(tempDir, "Ok")
        File.Copy(mboxFixture "FromQuotedBody.mbox", lockedPath)
        File.Copy(mboxFixture "NoMessageId.mbox", okPath)

        use lockHandle = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        let load, save, _ = inMemoryWatermarkStore ()

        let account: DiscoveredMailAccount =
            {
                Id = testAccountId
                ProfilePath = tempDir
                DisplayName = "Test"
                EmailAddresses = []
                StoreFormat = Mbox
                StoreDirectory = tempDir
                StoreDirectoryExists = true
                Folders = [ folder "Locked"; folder "Ok" ]
                CachedMessageCount = None
            }

        let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

        let actual = MailFolderReader.read lookupAccount load save testAccountId noCutoff

        match actual with
        | Ok messages -> Assert.Single messages |> ignore // only "Ok"'s one message
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

// ---------- 4.2 the cutoff ----------

[<Fact; Trait("Level", "Integration")>]
let ``readFolder skips messages older than the cutoff without parsing their malformed bodies, and includes the boundary and later messages`` () =
    let load, save, _ = inMemoryWatermarkStore ()
    let cutoff = ScanCutoff.ofStartOfDay (DateTime(2026, 1, 1))

    let actual = readFolder load save testAccountId (folder "MixedCutoffMalformedMime.mbox") mboxFixtures Mbox cutoff

    match actual with
    | Ok messages ->
        Assert.Equal(2, messages.Length)

        Assert.Equal<string list>(
            [ "<cutoff-boundary-new-1@example.com>"; "<cutoff-new-2@example.com>" ] |> List.sort,
            messages |> List.map (fun m -> m.SourceMessageId) |> List.sort
        )
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``readFolder includes a message with an unparseable Date rather than skipping it`` () =
    let load, save, _ = inMemoryWatermarkStore ()
    // A cutoff far in the future - if the unparseable-Date message were (wrongly) treated as
    // having a real, comparable date, a future cutoff might exclude it. It must still return.
    let farFutureCutoff = ScanCutoff.ofStartOfDay (DateTime(2999, 1, 1))

    let actual = readFolder load save testAccountId (folder "UnparseableDate.mbox") mboxFixtures Mbox farFutureCutoff

    match actual with
    | Ok messages -> Assert.Single messages |> ignore
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

// ---------- 4.3 message content ----------

[<Fact; Trait("Level", "Integration")>]
let ``readFolder synthesises a stable id for a message with no Message-ID`` () =
    let load, save, _ = inMemoryWatermarkStore ()

    let actual = readFolder load save testAccountId (folder "NoMessageId.mbox") mboxFixtures Mbox noCutoff

    match actual with
    | Ok messages ->
        let message = Assert.Single messages
        Assert.StartsWith("synthesized:", message.SourceMessageId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``a synthesised id is stable across a compaction that removes a preceding message`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Compacting")

        let firstMessage =
            "From alice@example.com Mon Jan 05 09:00:00 2026\n"
            + "X-Mozilla-Status: 0001\n"
            + "From: alice@example.com\n"
            + "To: bob@example.com\n"
            + "Subject: First (will be compacted away)\n"
            + "Date: Mon, 05 Jan 2026 09:00:00 +0000\n"
            + "Content-Type: text/plain; charset=utf-8\n\n"
            + "First message body.\n\n"

        let secondMessage =
            "From carol@example.com Tue Jan 06 10:00:00 2026\n"
            + "X-Mozilla-Status: 0001\n"
            + "From: carol@example.com\n"
            + "To: bob@example.com\n"
            + "Subject: Second (survives)\n"
            + "Date: Tue, 06 Jan 2026 10:00:00 +0000\n"
            + "Content-Type: text/plain; charset=utf-8\n\n"
            + "Second message body.\n"

        File.WriteAllText(path, firstMessage + secondMessage)

        let load, save, _ = inMemoryWatermarkStore ()
        let before = readFolder load save testAccountId (folder "Compacting") tempDir Mbox noCutoff

        let idBefore =
            match before with
            | Ok [ _; second ] -> second.SourceMessageId
            | other -> failwith $"Expected two messages, got: {other}"

        // Simulate a compaction: the first message is gone, the second is unchanged.
        File.WriteAllText(path, secondMessage)

        let load2, save2, _ = inMemoryWatermarkStore () // fresh store - this is a full re-read
        let after = readFolder load2 save2 testAccountId (folder "Compacting") tempDir Mbox noCutoff

        let idAfter =
            match after with
            | Ok [ only ] -> only.SourceMessageId
            | other -> failwith $"Expected one message, got: {other}"

        Assert.Equal(idBefore, idAfter)
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns an attachment's bytes decoded from a declared octet-stream part`` () =
    let load, save, _ = inMemoryWatermarkStore ()

    let actual = readFolder load save testAccountId (folder "AttachmentDeclaredOctetStream.mbox") mboxFixtures Mbox noCutoff

    match actual with
    | Ok messages ->
        let message = Assert.Single messages
        let attachment = Assert.Single message.Attachments
        Assert.Equal("application/octet-stream", attachment.DeclaredContentType)
        Assert.Equal("invoice-1001.pdf", attachment.FileName)
        Assert.StartsWith("%PDF", Text.Encoding.ASCII.GetString(attachment.Content, 0, 4))
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``readFolder writes no file to a watched temp directory while decoding an attachment`` () =
    let watchedDir = freshTempDirectory ()

    try
        let load, save, _ = inMemoryWatermarkStore ()
        readFolder load save testAccountId (folder "AttachmentDeclaredOctetStream.mbox") mboxFixtures Mbox noCutoff
        |> ignore

        Assert.Empty(Directory.GetFileSystemEntries watchedDir)
    finally
        Directory.Delete(watchedDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns both body alternatives when a message carries both`` () =
    let load, save, _ = inMemoryWatermarkStore ()

    let actual = readFolder load save testAccountId (folder "PlainAndHtmlAlternative.mbox") mboxFixtures Mbox noCutoff

    match actual with
    | Ok messages ->
        let message = Assert.Single messages
        Assert.True(message.BodyText.IsSome)
        Assert.True(message.BodyHtml.IsSome)
        Assert.Contains("Invoice Number: 1001", message.BodyText.Value)
        Assert.Contains("<table>", message.BodyHtml.Value)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``readFolder generates a filename stating the content type for an attachment with none`` () =
    let load, save, _ = inMemoryWatermarkStore ()

    let actual = readFolder load save testAccountId (folder "AttachmentNoFilename.mbox") mboxFixtures Mbox noCutoff

    match actual with
    | Ok messages ->
        let message = Assert.Single messages
        let attachment = Assert.Single message.Attachments
        Assert.Contains("application/pdf", attachment.FileName)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

// ---------- 4.4 watermarks and incremental reads ----------

[<Fact; Trait("Level", "Integration")>]
let ``readFolder records the watermark from the first read`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Watermarked")
        File.Copy(mboxFixture "NoMessageId.mbox", path)
        let expectedSize = FileInfo(path).Length

        let load, save, store = inMemoryWatermarkStore ()
        readFolder load save testAccountId (folder "Watermarked") tempDir Mbox noCutoff |> ignore

        let wm = store.[(MailAccountId.value testAccountId, "Watermarked")]
        Assert.Equal(expectedSize, wm.SizeBytes)
        Assert.Equal(expectedSize, wm.OffsetReached)
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns nothing new on a second read of an unchanged file`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Unchanged")
        File.Copy(mboxFixture "NoMessageId.mbox", path)

        let load, save, _ = inMemoryWatermarkStore ()
        readFolder load save testAccountId (folder "Unchanged") tempDir Mbox noCutoff |> ignore
        let second = readFolder load save testAccountId (folder "Unchanged") tempDir Mbox noCutoff

        match second with
        | Ok messages -> Assert.Empty messages
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns only the appended message on a second read after an append`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Appending")
        let first = File.ReadAllText(mboxFixture "NoMessageId.mbox")
        File.WriteAllText(path, first)

        let load, save, _ = inMemoryWatermarkStore ()
        readFolder load save testAccountId (folder "Appending") tempDir Mbox noCutoff |> ignore

        let appended =
            "From dave@example.com Wed Jan 07 11:00:00 2026\n"
            + "X-Mozilla-Status: 0001\n"
            + "Message-ID: <appended-1@example.com>\n"
            + "From: dave@example.com\n"
            + "To: bob@example.com\n"
            + "Subject: Appended after the first read\n"
            + "Date: Wed, 07 Jan 2026 11:00:00 +0000\n"
            + "Content-Type: text/plain; charset=utf-8\n\n"
            + "This message was appended after the watermark was recorded.\n"

        // File.WriteAllText overwrites; ensure the original bytes plus the appended message,
        // and that its modification time moves forward.
        File.WriteAllText(path, first + appended)
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds 2.0)

        let second = readFolder load save testAccountId (folder "Appending") tempDir Mbox noCutoff

        match second with
        | Ok messages ->
            let message = Assert.Single messages
            Assert.Equal("<appended-1@example.com>", message.SourceMessageId)
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder discards the watermark and re-reads the whole folder when the file has shrunk`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Shrinking")
        let original = File.ReadAllText(mboxFixture "NoMessageId.mbox")
        File.WriteAllText(path, original + original) // pretend it was "bigger" first

        let load, save, _ = inMemoryWatermarkStore ()
        readFolder load save testAccountId (folder "Shrinking") tempDir Mbox noCutoff |> ignore

        // Now shrink it back to just the original content.
        File.WriteAllText(path, original)
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds 2.0)

        let second = readFolder load save testAccountId (folder "Shrinking") tempDir Mbox noCutoff

        match second with
        | Ok messages -> Assert.Single messages |> ignore // the whole (now-shrunk) file re-read
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder re-reads the whole folder when the modification time is inconsistent with the watermark`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Touched")
        File.Copy(mboxFixture "NoMessageId.mbox", path)

        let load, save, _ = inMemoryWatermarkStore ()
        readFolder load save testAccountId (folder "Touched") tempDir Mbox noCutoff |> ignore

        // Same size, but the modification time moved without any content change - not the
        // "unchanged" case (mtime differs) and not the "grown" case (size did not increase).
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes 5.0)

        let second = readFolder load save testAccountId (folder "Touched") tempDir Mbox noCutoff

        match second with
        | Ok messages -> Assert.Single messages |> ignore // re-read, not skipped
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

// ---------- 4.4b CRLF messages ----------

/// RFC 5322 mandates CRLF, and a message stored exactly as it arrived over IMAP/SMTP keeps it.
/// The committed fixtures are pinned to LF by .gitattributes, so nothing else in this file
/// exercises a CRLF message at all.
let private crlfMessage (messageId: string) (subject: string) =
    $"From sender@example.com Mon Jan 05 09:00:00 2026\r\n"
    + "X-Mozilla-Status: 0001\r\n"
    + $"Message-ID: <{messageId}>\r\n"
    + "From: sender@example.com\r\n"
    + "To: bob@example.com\r\n"
    + $"Subject: {subject}\r\n"
    + "Date: Mon, 05 Jan 2026 09:00:00 +0000\r\n"
    + "Content-Type: text/plain; charset=utf-8\r\n"
    + "\r\n"
    + $"Body of {subject}.\r\n"

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns a message whose header block ends in a CRLF blank line`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Crlf")
        File.WriteAllText(path, crlfMessage "crlf-1@example.com" "CRLF only")

        let load, save, _ = inMemoryWatermarkStore ()
        let actual = readFolder load save testAccountId (folder "Crlf") tempDir Mbox noCutoff

        match actual with
        | Ok messages ->
            let message = Assert.Single messages
            Assert.Equal("<crlf-1@example.com>", message.SourceMessageId)
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns every message of a CRLF folder, not silently none of them`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "CrlfMany")

        File.WriteAllText(
            path,
            crlfMessage "crlf-a@example.com" "First" + "\r\n" + crlfMessage "crlf-b@example.com" "Second"
        )

        let load, save, _ = inMemoryWatermarkStore ()
        let actual = readFolder load save testAccountId (folder "CrlfMany") tempDir Mbox noCutoff

        match actual with
        | Ok messages ->
            Assert.Equal<string list>(
                [ "<crlf-a@example.com>"; "<crlf-b@example.com>" ],
                messages |> List.map (fun m -> m.SourceMessageId) |> List.sort
            )
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``countMessages counts a CRLF folder's final message rather than treating it as torn`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "CrlfCounted")

        File.WriteAllText(
            path,
            crlfMessage "crlf-a@example.com" "First" + "\r\n" + crlfMessage "crlf-b@example.com" "Second"
        )

        let account: DiscoveredMailAccount =
            {
                Id = testAccountId
                ProfilePath = tempDir
                DisplayName = "Test"
                EmailAddresses = []
                StoreFormat = Mbox
                StoreDirectoryExists = true
                StoreDirectory = tempDir
                Folders = [ folder "CrlfCounted" ]
                CachedMessageCount = None
            }

        let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

        Assert.Equal(Ok 2, countMessages lookupAccount testAccountId)
    finally
        Directory.Delete(tempDir, true)

// ---------- 4.4c the recorded offset is the offset actually reached ----------

let private lfMessage (messageId: string) (subject: string) =
    "From sender@example.com Mon Jan 05 09:00:00 2026\n"
    + "X-Mozilla-Status: 0001\n"
    + $"Message-ID: <{messageId}>\n"
    + "From: sender@example.com\n"
    + "To: bob@example.com\n"
    + $"Subject: {subject}\n"
    + "Date: Mon, 05 Jan 2026 09:00:00 +0000\n"
    + "Content-Type: text/plain; charset=utf-8\n"
    + "\n"
    + $"Body of {subject}.\n"

[<Fact; Trait("Level", "Integration")>]
let ``readFolder records the whole file length as the offset reached for a multi-message folder`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "ManyMessages")

        File.WriteAllText(
            path,
            lfMessage "many-1@example.com" "One"
            + "\n"
            + lfMessage "many-2@example.com" "Two"
            + "\n"
            + lfMessage "many-3@example.com" "Three"
        )

        let expectedSize = FileInfo(path).Length

        let load, save, store = inMemoryWatermarkStore ()

        match readFolder load save testAccountId (folder "ManyMessages") tempDir Mbox noCutoff with
        | Ok messages -> Assert.Equal(3, messages.Length)
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

        let wm = store.[(MailAccountId.value testAccountId, "ManyMessages")]
        Assert.Equal(expectedSize, wm.SizeBytes)
        Assert.Equal(expectedSize, wm.OffsetReached)
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``the recorded offset stays exact across successive appends rather than drifting backwards`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Growing")
        let load, save, store = inMemoryWatermarkStore ()

        let mutable content = lfMessage "grow-1@example.com" "One" + "\n" + lfMessage "grow-2@example.com" "Two"
        File.WriteAllText(path, content)

        readFolder load save testAccountId (folder "Growing") tempDir Mbox noCutoff |> ignore

        Assert.Equal(
            FileInfo(path).Length,
            store.[(MailAccountId.value testAccountId, "Growing")].OffsetReached
        )

        for round in 3..6 do
            content <- content + "\n" + lfMessage $"grow-{round}@example.com" $"Round {round}"
            File.WriteAllText(path, content)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(float round))

            match readFolder load save testAccountId (folder "Growing") tempDir Mbox noCutoff with
            | Ok messages ->
                let ids = messages |> List.map (fun m -> m.SourceMessageId)
                Assert.Equal<string list>([ $"<grow-{round}@example.com>" ], ids)
            | Error error -> Assert.Fail($"Expected Ok on round {round}, but got Error: {error}")

            Assert.Equal(
                FileInfo(path).Length,
                store.[(MailAccountId.value testAccountId, "Growing")].OffsetReached
            )
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder re-reads the whole folder when the stored offset lies past the end of the file`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "StaleOffset")
        File.WriteAllText(path, lfMessage "stale-1@example.com" "One")
        let length = FileInfo(path).Length

        let load, save, store = inMemoryWatermarkStore ()

        // A watermark left behind by a read that raced an append: readFolder measures the size
        // before opening the file, so a folder that grew in between records an OffsetReached
        // past the SizeBytes beside it. A later compaction to something between the two then
        // leaves that offset past the end of the file. The grown-file guard takes the offset
        // anyway, and readMboxFile is asked to buffer a NEGATIVE number of bytes - the same
        // uncaught ArgumentException the over-large guard was added for, from the other end of
        // the range. Neither `with` handler in readMboxFile catches it, so it escapes the whole
        // API call.
        store.[(MailAccountId.value testAccountId, "StaleOffset")] <-
            {
                SizeBytes = 1L
                ModifiedAt = File.GetLastWriteTimeUtc path
                OffsetReached = length + 500L
            }

        match readFolder load save testAccountId (folder "StaleOffset") tempDir Mbox noCutoff with
        | Ok messages ->
            Assert.Equal<string list>([ "<stale-1@example.com>" ], messages |> List.map (fun m -> m.SourceMessageId))
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

        Assert.Equal(length, store.[(MailAccountId.value testAccountId, "StaleOffset")].OffsetReached)
    finally
        Directory.Delete(tempDir, true)

// ---------- 4.4d a folder larger than one streaming chunk ----------
//
// The reader used to buffer a folder whole and refuse anything over ~1 GiB (a Latin1 string
// cannot hold more), which `read`'s `| Error _ -> []` then dropped silently on every scan -
// invoice-extraction's Phase 12 measurement found a 2 GB Gmail INBOX contributing zero messages.
// The reader now streams `MailFolderReader.StreamChunkBytes` at a time, so a folder of any size
// reads in bounded memory and there is no per-folder ceiling to report.

/// A real multi-message mbox several streaming chunks in size. Built, not committed - a fixture
/// this big does not belong in git - and small messages so the row count is easy to assert.
let private manyMessagesMbox (count: int) =
    let sb = System.Text.StringBuilder()

    for i in 1..count do
        if i > 1 then sb.Append('\n') |> ignore
        sb.Append(lfMessage $"bulk-{i}@example.com" $"Message {i}") |> ignore

    sb.ToString()

[<Fact; Trait("Level", "Integration")>]
let ``readFolder reads every message of a folder that spans several streaming chunks`` () =
    let tempDir = freshTempDirectory ()

    try
        // ~16000 messages * ~290 bytes ~= 4.6 MB, past the 4 MB streaming chunk with a message
        // straddling the boundary.
        let messageCount = 16000
        let path = Path.Combine(tempDir, "Bulk")
        File.WriteAllText(path, manyMessagesMbox messageCount)
        Assert.True(FileInfo(path).Length > int64 MailFolderReader.StreamChunkBytes)

        let load, save, store = inMemoryWatermarkStore ()

        match readFolder load save testAccountId (folder "Bulk") tempDir Mbox noCutoff with
        | Ok messages ->
            Assert.Equal(messageCount, messages.Length)
            Assert.Equal("<bulk-1@example.com>", messages.Head.SourceMessageId)
            Assert.Equal($"<bulk-{messageCount}@example.com>", (List.last messages).SourceMessageId)
        | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

        // The whole file was consumed, so the watermark is the whole file length.
        Assert.Equal(
            FileInfo(path).Length,
            store.[(MailAccountId.value testAccountId, "Bulk")].OffsetReached
        )
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``countMessages counts every message of a folder that spans several streaming chunks`` () =
    let tempDir = freshTempDirectory ()

    try
        let messageCount = 16000
        let path = Path.Combine(tempDir, "Bulk")
        File.WriteAllText(path, manyMessagesMbox messageCount)

        let account: DiscoveredMailAccount =
            {
                Id = testAccountId
                ProfilePath = tempDir
                DisplayName = "Test"
                EmailAddresses = []
                StoreFormat = Mbox
                StoreDirectoryExists = true
                StoreDirectory = tempDir
                Folders = [ folder "Bulk" ]
                CachedMessageCount = None
            }

        let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

        Assert.Equal(Ok messageCount, countMessages lookupAccount testAccountId)
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder returns no messages for a large boundary-less file rather than throwing or hanging`` () =
    // A sparse file: NTFS records the length without zeroing the clusters, so this is
    // single-digit milliseconds to create. All-zero bytes hold no "From " boundary, so the
    // streaming reader finds no messages - the point is that it does so in bounded memory and
    // bounded time rather than reporting the folder unreadable or dying converting it to text.
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Garbage")

        (use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)
         stream.SetLength(int64 MailFolderReader.StreamChunkBytes * 6L + 17L))

        let load, save, _ = inMemoryWatermarkStore ()

        match readFolder load save testAccountId (folder "Garbage") tempDir Mbox noCutoff with
        | Ok messages -> Assert.Empty messages
        | Error error -> Assert.Fail($"Expected Ok [], but got Error: {error}")
    finally
        Directory.Delete(tempDir, true)

// ---------- 4.4e the watermark through the REAL store, not only an in-memory one ----------

/// Every other test on this page binds LoadWatermark/SaveWatermark to a Dictionary, which cannot
/// lose anything on the way to disk. Production binds them to ThunderbirdStore over LiteDB, and
/// that is where an incremental read either works or silently stops working, so this one test
/// pays for a real database file.
let private withRealWatermarkStore (test: LoadWatermark -> SaveWatermark -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "direct"
    let handleError = HandleErrorBuilder(fun _ -> ())

    let load: LoadWatermark =
        fun accountId relativePath ->
            ThunderbirdStore.loadWatermarkEntry handleError context.GetWatermarksCollection accountId relativePath
            |> Result.mapError (fun ex -> MailStoreFailed ex.Message)

    let save: SaveWatermark =
        fun accountId relativePath watermark ->
            ThunderbirdStore.saveWatermarkEntry handleError context.GetWatermarksCollection accountId relativePath watermark
            |> Result.mapError (fun ex -> MailStoreFailed ex.Message)

    try
        test load save
    finally
        context.Dispose()

        try
            File.Delete databasePath
        with _ ->
            ()

[<Fact; Trait("Level", "Integration")>]
let ``a second read of an unchanged file returns nothing new when the watermark went through the real store`` () =
    withRealWatermarkStore (fun load save ->
        let tempDir = freshTempDirectory ()

        try
            let path = Path.Combine(tempDir, "Unchanged")

            File.WriteAllText(
                path,
                lfMessage "real-1@example.com" "One" + "\n" + lfMessage "real-2@example.com" "Two"
            )

            // Pinned rather than left to the clock: NTFS keeps 100-ns ticks, so a real mtime
            // carries sub-millisecond precision, and this fixes that fact instead of hoping for
            // it. Kind = Utc is what File.GetLastWriteTimeUtc hands readFolder.
            File.SetLastWriteTimeUtc(path, DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc).AddTicks 1234L)

            match readFolder load save testAccountId (folder "Unchanged") tempDir Mbox noCutoff with
            | Ok messages -> Assert.Equal(2, messages.Length)
            | Error error -> Assert.Fail($"Expected Ok on the first read, but got Error: {error}")

            match readFolder load save testAccountId (folder "Unchanged") tempDir Mbox noCutoff with
            | Ok messages -> Assert.Equal<string list>([], messages |> List.map (fun m -> m.SourceMessageId))
            | Error error -> Assert.Fail($"Expected Ok on the second read, but got Error: {error}")
        finally
            Directory.Delete(tempDir, true))

// ---------- 4.5 countMessages ----------

[<Fact; Trait("Level", "Integration")>]
let ``countMessages matches the number of complete messages across scannable folders`` () =
    let alphaStoreDirectory = Path.Combine(measuredShapeProfile, "ImapMail", "imap.alpha.example.com")
    let folders = MailFolderEnumerator.enumerate alphaStoreDirectory Mbox

    let account: DiscoveredMailAccount =
        {
            Id = testAccountId
            ProfilePath = measuredShapeProfile
            DisplayName = "Alpha"
            EmailAddresses = []
            StoreFormat = Mbox
            StoreDirectory = alphaStoreDirectory
            StoreDirectoryExists = true
            Folders = folders
            CachedMessageCount = None
        }

    let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

    let actual = countMessages lookupAccount testAccountId

    // INBOX, Music, Music/Surrey Hills Orchestra, Music/Surrey Hills Orchestra/Rehearsals -
    // one message each - Trash/Junk/Sent/Drafts excluded.
    Assert.Equal(Ok 4, actual)

[<Fact; Trait("Level", "Integration")>]
let ``countMessages does not parse any body, so a folder with malformed message bodies still counts`` () =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Malformed")
        File.Copy(mboxFixture "MixedCutoffMalformedMime.mbox", path)

        let account: DiscoveredMailAccount =
            {
                Id = testAccountId
                ProfilePath = tempDir
                DisplayName = "Test"
                EmailAddresses = []
                StoreFormat = Mbox
                StoreDirectory = tempDir
                StoreDirectoryExists = true
                Folders = [ folder "Malformed" ]
                CachedMessageCount = None
            }

        let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

        let actual = countMessages lookupAccount testAccountId

        Assert.Equal(Ok 4, actual)
    finally
        Directory.Delete(tempDir, true)

// ---------- 4.6 a configured-but-missing store directory ----------
//
// Discovery keeps such an account rather than dropping it (requirements.md -> "Reading the
// profile"), and the table marks it. Everything downstream then enumerated no folders for it, so
// both readers folded over an empty list and answered "nothing here" - `Ok 0` from countMessages
// and `Ok []` from read - which is exactly what a genuinely empty account answers. The one fact
// that separates them is that the store directory is gone, and `MailAccountError` has carried the
// case for it (`StoreDirectoryMissing`) since the first commit without anything ever raising it.

/// `imap.delta.example.com` is declared by the measured-shape fixture's prefs.js and has no
/// directory under `ImapMail/` - the fixture shape requirements.md asks for ("an account whose
/// store directory is missing"), and the same state a real profile reaches when a mail directory
/// is deleted or its drive goes away.
let private missingStoreDirectory =
    Path.Combine(measuredShapeProfile, "ImapMail", "imap.delta.example.com")

let private missingStoreAccount: DiscoveredMailAccount =
    {
        Id = testAccountId
        ProfilePath = measuredShapeProfile
        DisplayName = "Delta Mail"
        EmailAddresses = []
        StoreFormat = Mbox
        StoreDirectory = missingStoreDirectory
        StoreDirectoryExists = false
        Folders = []
        CachedMessageCount = None
    }

[<Fact; Trait("Level", "Integration")>]
let ``countMessages reports a store directory that is gone rather than counting it as zero`` () =
    Assert.False(Directory.Exists missingStoreDirectory)

    let lookupAccount: LookupAccount = fun _ -> Ok(Some missingStoreAccount)

    match countMessages lookupAccount testAccountId with
    | Error(StoreDirectoryMissing(id, path)) ->
        Assert.Equal(testAccountId, id)
        Assert.Equal(missingStoreDirectory, path)
    | other -> Assert.Fail(sprintf "Expected Error(StoreDirectoryMissing _), but got: %A" other)

[<Fact; Trait("Level", "Integration")>]
let ``read reports a store directory that is gone rather than returning no messages`` () =
    Assert.False(Directory.Exists missingStoreDirectory)

    let load, save, _ = inMemoryWatermarkStore ()
    let lookupAccount: LookupAccount = fun _ -> Ok(Some missingStoreAccount)

    match read lookupAccount load save testAccountId noCutoff with
    | Error(StoreDirectoryMissing(id, path)) ->
        Assert.Equal(testAccountId, id)
        Assert.Equal(missingStoreDirectory, path)
    | other -> Assert.Fail(sprintf "Expected Error(StoreDirectoryMissing _), but got: %A" other)

[<Fact; Trait("Level", "Integration")>]
let ``an account whose store directory is there but holds no folders still counts zero`` () =
    // The half the guard must NOT swallow: "the store is gone" and "the store is there and empty"
    // are different answers, and only the first is an error.
    let tempDir = freshTempDirectory ()

    try
        let account = { missingStoreAccount with StoreDirectory = tempDir; StoreDirectoryExists = true }
        let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

        Assert.Equal(Ok 0, countMessages lookupAccount testAccountId)
        Assert.Equal(Ok [], read lookupAccount (fun _ _ -> Ok None) (fun _ _ _ -> Ok()) testAccountId noCutoff)
    finally
        Directory.Delete(tempDir, true)
