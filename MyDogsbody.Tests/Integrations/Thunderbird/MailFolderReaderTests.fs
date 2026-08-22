module MyDogsbody.Tests.Integrations.Thunderbird.MailFolderReaderTests

open System
open System.Collections.Generic
open System.IO
open Xunit
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
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
