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

// ---------- 4.0 bufferSpan: the two size guards, exercised without a file ----------

/// `bufferSpan` is pure, so every value the guards turn on is reachable here directly - including
/// the ones no committed fixture could carry. The over-limit decision reached production untested
/// once already, and the negative-span one crashed out of the whole API call.
let private oneByteOverTheLimit = int64 MaxBufferableBytes + 1L

/// The constant is a hard runtime ceiling, not a taste. `splitIntoMessages` turns the whole
/// buffered span into a Latin1 string, and .NET caps a string at 1,073,741,791 chars - the 2 GB
/// object-size limit at two bytes per char, which is HALF what `Array.MaxLength` allows. Asking
/// for one char more is rejected by the runtime on the size alone, so this costs nothing to
/// assert and pins the constant to the ceiling it was chosen from: if a future runtime lowers
/// that ceiling, this fails here rather than as an OutOfMemoryException in production.
[<Fact; Trait("Level", "Unit")>]
let ``the buffer limit is the largest string .NET can build, so one byte more could not be turned into text`` () =
    Assert.Throws<OutOfMemoryException>(Action(fun () -> String('a', MaxBufferableBytes + 1) |> ignore)) |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan buffers the whole file when nothing has been read yet`` () =
    Assert.Equal(Ok(0L, 1000), bufferSpan 1000L 0L)

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan buffers only what follows a stored offset inside the file`` () =
    Assert.Equal(Ok(400L, 600), bufferSpan 1000L 400L)

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan buffers nothing when the stored offset is exactly the end of the file`` () =
    Assert.Equal(Ok(1000L, 0), bufferSpan 1000L 1000L)

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan buffers nothing for an empty file`` () =
    Assert.Equal(Ok(0L, 0), bufferSpan 0L 0L)

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan restarts at zero when the stored offset lies past the end of the file`` () =
    // Not an error: the file cannot contain what the watermark claims, and re-reading a message
    // is recoverable where a negative-length allocation is not.
    Assert.Equal(Ok(0L, 1000), bufferSpan 1000L 1500L)

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan restarts at zero for a negative stored offset`` () =
    Assert.Equal(Ok(0L, 1000), bufferSpan 1000L -1L)

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan accepts a span of exactly the limit`` () =
    Assert.Equal(Ok(0L, MaxBufferableBytes), bufferSpan (int64 MaxBufferableBytes) 0L)

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan reports a span one byte past the limit, naming both sizes`` () =
    Assert.Equal(
        Error
            $"The folder has {oneByteOverTheLimit} bytes still to read, more than this reader can buffer in one pass ({MaxBufferableBytes} bytes).",
        bufferSpan oneByteOverTheLimit 0L
    )

/// `Array.MaxLength` is what the guard used to be set to, and it is nearly twice the limit - a
/// span that size was accepted, buffered, and then killed the process converting it to text.
[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan reports a span of the largest array, which is far past the limit`` () =
    Assert.Equal(
        Error
            $"The folder has {int64 Array.MaxLength} bytes still to read, more than this reader can buffer in one pass ({MaxBufferableBytes} bytes).",
        bufferSpan (int64 Array.MaxLength) 0L
    )

[<Fact; Trait("Level", "Unit")>]
let ``bufferSpan measures the span from the stored offset, so an already-mostly-read large folder still reads`` () =
    // The span, not the file, is what has to fit an array: a 3 GB folder read up to 2 GB has
    // 1 GB left, and refusing it because the file is large would stop an incremental read dead.
    Assert.Equal(Ok(2_000_000_000L, 1_000_000_000), bufferSpan 3_000_000_000L 2_000_000_000L)

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

// ---------- 4.4d a folder too large to buffer in one pass ----------

/// A file of `sizeBytes` whose bytes are never written: NTFS records the length without zeroing
/// the clusters, so this costs single-digit milliseconds and no measurable I/O. That is what
/// makes the over-limit guards testable at all - round 2 added one and shipped it untested,
/// stating a fixture that large could not be committed, which is true of a *committed* fixture
/// but not of one the test makes and deletes.
let private withOversizedFolder (test: string -> string -> unit) =
    let tempDir = freshTempDirectory ()

    try
        let path = Path.Combine(tempDir, "Oversized")

        (use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)
         // One byte past what this reader can buffer, so the guard is exercised at its boundary
         // rather than somewhere comfortably beyond it. This size matters: rounds 2 and 3 set
         // the guard at `Array.MaxLength`, nearly twice the real ceiling, so a folder of exactly
         // this many bytes sailed past it - `countMessages` answered `Ok 0` and `readFolder`
         // threw an uncaught OutOfMemoryException out of the whole API, which are precisely the
         // two defects those rounds reported as closed.
         stream.SetLength(int64 MaxBufferableBytes + 1L))

        test tempDir path
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``readFolder reports a folder too large to buffer in one pass rather than throwing`` () =
    withOversizedFolder (fun tempDir path ->
        let load, save, store = inMemoryWatermarkStore ()

        match readFolder load save testAccountId (folder "Oversized") tempDir Mbox noCutoff with
        | Error(MailFolderUnreadable(reportedPath, reason)) ->
            Assert.Equal(path, reportedPath)

            Assert.Equal(
                $"The folder has {int64 MaxBufferableBytes + 1L} bytes still to read, more than this reader can buffer in one pass ({MaxBufferableBytes} bytes).",
                reason
            )
        | Error other -> Assert.Fail($"Expected MailFolderUnreadable, but got Error: {other}")
        | Ok messages -> Assert.Fail($"Expected Error, but got Ok with {messages.Length} messages")

        // Nothing was read, so nothing may claim to have been: a watermark written here would
        // make the next scan resume past bytes this reader never saw.
        Assert.False(store.ContainsKey(MailAccountId.value testAccountId, "Oversized")))

[<Fact; Trait("Level", "Integration")>]
let ``countMessages reports a folder too large to buffer rather than silently counting it as zero`` () =
    withOversizedFolder (fun tempDir path ->
        let account: DiscoveredMailAccount =
            {
                Id = testAccountId
                ProfilePath = tempDir
                DisplayName = "Test"
                EmailAddresses = []
                StoreFormat = Mbox
                StoreDirectory = tempDir
                StoreDirectoryExists = true
                Folders = [ folder "Oversized" ]
                CachedMessageCount = None
            }

        let lookupAccount: LookupAccount = fun _ -> Ok(Some account)

        match countMessages lookupAccount testAccountId with
        | Error(MailFolderUnreadable(reportedPath, reason)) ->
            Assert.Equal(path, reportedPath)

            Assert.Equal(
                $"The folder has {int64 MaxBufferableBytes + 1L} bytes still to read, more than this reader can buffer in one pass ({MaxBufferableBytes} bytes).",
                reason
            )
        | Error other -> Assert.Fail($"Expected MailFolderUnreadable, but got Error: {other}")
        | Ok count ->
            Assert.Fail(
                $"Expected Error, but got Ok {count} - a folder that could not be read was counted as if it held that many messages"
            ))

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
