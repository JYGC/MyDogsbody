module MyDogsbody.Integrations.Thunderbird.MailFolderReader

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open MimeKit
open MimeKit.Utils
open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

/// A folder's watermark: the file's size and last-write time when last read, and the byte
/// offset reached. Integration-internal - the domain only ever sees `ClearWatermarks`.
type FolderWatermark =
    {
        SizeBytes: int64
        ModifiedAt: DateTime
        OffsetReached: int64
    }

type LookupAccount = MailAccountId -> Result<DiscoveredMailAccount option, MailAccountError>
type LoadWatermark = MailAccountId -> string -> Result<FolderWatermark option, MailAccountError>
type SaveWatermark = MailAccountId -> string -> FolderWatermark -> Result<unit, MailAccountError>

/// Every byte of an mbox file maps 1:1 to a Latin1 char and back, so splitting on textual line
/// boundaries can never corrupt a message's own declared (possibly multi-byte) charset - the
/// exact original bytes are recovered by Latin1.GetBytes on the way back out.
let private latin1 = Encoding.Latin1

let private dateHeaderPattern = Regex(@"(?im)^Date:[ \t]*(?<value>.+?)\r?$", RegexOptions.Compiled)
let private messageIdHeaderPattern = Regex(@"(?im)^Message-ID:[ \t]*(?<value>.+?)\r?$", RegexOptions.Compiled)

let private tryParseHeaderDate (headerBlock: string) : DateTimeOffset option =
    let m = dateHeaderPattern.Match headerBlock

    if not m.Success then
        None
    else
        match DateUtils.TryParse(m.Groups.["value"].Value) with
        | true, date -> Some date
        | false, _ -> None

/// A stable identifier for a message with no Message-ID header - a hash of the header block
/// text, not a byte offset, so it survives a compaction that removes a preceding message
/// (design.md -> Decisions taken #6).
let private synthesizeMessageId (headerBlock: string) : string =
    let bytes = SHA256.HashData(latin1.GetBytes headerBlock)
    "synthesized:" + Convert.ToHexString(bytes).ToLowerInvariant()

let private messageIdOf (headerBlock: string) : string =
    let m = messageIdHeaderPattern.Match headerBlock
    if m.Success && not (String.IsNullOrWhiteSpace m.Groups.["value"].Value) then
        m.Groups.["value"].Value.Trim()
    else
        synthesizeMessageId headerBlock

/// The index just past the blank line separating headers from body, or None if the segment has
/// no such separator at all - which for the LAST message in a file means it is torn: Thunderbird
/// was still writing it.
///
/// BOTH line endings are recognised, and that is load-bearing rather than defensive. RFC 5322
/// mandates CRLF, and a message written to the store exactly as it arrived over IMAP or SMTP
/// keeps it, so a folder can hold CRLF messages whatever the file's own convention is. Looking
/// only for "\n\n" made every such message report "no separator", which `classifySegment` turns
/// into a silently dropped message and a torn-message offset - data loss with nothing on screen
/// to show for it. Whichever separator appears first wins, so a CRLF header block followed by an
/// LF blank line inside the body still splits at the header block.
let private headerBlockAndRest (text: string) : (string * string) option =
    let lfIndex = text.IndexOf("\n\n", StringComparison.Ordinal)
    let crlfIndex = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)

    let separator =
        match lfIndex, crlfIndex with
        | -1, -1 -> None
        | -1, crlf -> Some(crlf, 4)
        | lf, -1 -> Some(lf, 2)
        | lf, crlf when lf <= crlf -> Some(lf, 2)
        | _, crlf -> Some(crlf, 4)

    separator
    |> Option.map (fun (index, separatorLength) -> text.Substring(0, index), text.Substring(index + separatorLength))

/// The largest a single message may be and still be turned into text.
///
/// It is NOT `Array.MaxLength`. Every step past the raw bytes works on a Latin1 string, and .NET
/// caps a string at 1,073,741,791 chars - the 2 GB object-size ceiling at two bytes per char,
/// a little under HALF what an array may hold. A `From `-delimited segment larger than this
/// cannot be stringified, so it is skipped and the offset advanced past it (see
/// `foldMboxSegments`). A real email never approaches this - Gmail caps attachments at 25 MB and
/// even 50 MB base64'd is ~70 MB - so a segment this size is a corrupt file or a mis-split, not
/// a message the reader failed.
///
/// This used to gate the WHOLE folder: any INBOX over ~1 GiB was reported unreadable and then,
/// through `read`'s `| Error _ -> []`, silently dropped on every scan. `invoice-extraction`'s
/// Phase 12 measurement caught it - a 2.0 GB Gmail INBOX holding years of un-archived invoice
/// mail contributed zero messages with nothing on screen. The streaming reader below removes the
/// per-folder ceiling entirely; this constant now only bounds a single segment.
[<Literal>]
let MaxBufferableBytes = 1_073_741_791

/// The chunk the streaming reader pulls from the file at a time. Small enough that a folder of
/// any size stays in bounded memory (this plus at most one in-progress message), large enough
/// that a multi-GB folder is not millions of syscalls.
[<Literal>]
let StreamChunkBytes = 4_194_304

/// Once an in-progress message (no second boundary yet) passes this, it is not a message: a real
/// mbox message is at most tens of MB, so this is a corrupt file or a `From ` line the split
/// mistook for a boundary. It is emitted as an oversized segment - skipped, offset advanced -
/// and the reader then byte-scans forward for the next real boundary rather than accumulating a
/// gigabyte in memory.
[<Literal>]
let MaxMessageBytes = 134_217_728

/// Where a streaming read should actually start: the stored offset, unless it lies outside the
/// file. A watermark can outlive the bytes it pointed at - `readFolder` measures the size before
/// opening the file, so a folder that grew in between records an `OffsetReached` past its
/// `SizeBytes`, and a later compaction between the two leaves that offset past EOF; a negative
/// value is the same hazard from the other end. Either way the file cannot contain what the
/// watermark claims, so the read restarts at 0 - re-reading a message is recoverable, seeking
/// past EOF is not. Pure and public so the reset is unit-tested without a file.
let normalizeStartOffset (totalLength: int64) (fromOffset: int64) : int64 =
    if fromOffset < 0L || fromOffset > totalLength then 0L else fromOffset

/// "From " as bytes: F r o m space.
let private fromLineBytes = [| 70uy; 114uy; 111uy; 109uy; 32uy |]

let private startsWithFrom (bytes: byte[]) (at: int) : bool =
    at + fromLineBytes.Length <= bytes.Length
    && Array.forall2 (=) fromLineBytes bytes.[at .. at + fromLineBytes.Length - 1]

/// The byte offsets within `bytes` at which a message segment begins: every "From " line whose
/// predecessor line is blank - one immediately preceded by "\n\n" or "\n\r\n" - plus offset 0
/// when `bytes` itself starts with a "From " line.
///
/// Byte-scanned rather than string-split (the old `splitIntoMessages`), so a segment larger than
/// a .NET string is still located rather than throwing on the way in. A properly mbox-quoted
/// ">From " never matches; an unquoted "From " opening a body line preceded by a blank one does,
/// exactly as before - `tryParseMessage` is what holds the resulting fragment. The blank line has
/// to be VISIBLE in `bytes`: a "From " at offset 0 or 1 whose preceding newline was left in the
/// previous buffer is `foldMboxSegments`'s concern, not this function's.
let segmentStartOffsets (bytes: byte[]) : int list =
    let offsets = ResizeArray<int>()

    if startsWithFrom bytes 0 then
        offsets.Add 0

    // A boundary "From " begins at j+1, where bytes.[j] = '\n' and the line before it was blank:
    // bytes.[j-1] = '\n', or bytes.[j-1] = '\r' with bytes.[j-2] = '\n'.
    for j in 1 .. bytes.Length - 2 do
        if bytes.[j] = 10uy && startsWithFrom bytes (j + 1) then
            let blankLineBefore =
                bytes.[j - 1] = 10uy || (bytes.[j - 1] = 13uy && j >= 2 && bytes.[j - 2] = 10uy)

            if blankLineBefore then
                offsets.Add(j + 1)

    List.ofSeq offsets

/// Bytes kept as a rolling tail while seeking past an oversized segment, so a "\n\r\nFrom "
/// boundary split across two chunk reads is still recognised. Seven would do ("\r\nFrom " plus
/// the "\n" before it); eight is a round number with a byte to spare.
[<Literal>]
let private seekCarryBytes = 8

/// Walks an mbox stream one message segment at a time, in memory bounded by `chunkSize` plus one
/// in-progress message. `onSegment state absoluteStartOffset segmentBytes isLastInFile` is called
/// once per segment - the bytes from one "From " boundary to the next, or to EOF - and its
/// results are folded into `state`, which is returned.
///
/// The invariant: outside `seeking`, `pending` begins at a message boundary and `pendingStart`
/// is its absolute offset in the file. A segment larger than `maxMessageBytes` with no second
/// boundary is not a message (a corrupt file, or a body line the split mistook for a boundary):
/// it is emitted once so the caller can skip it, then bytes are discarded until the next real
/// boundary rather than accumulating without limit.
///
/// `chunkSize` and `maxMessageBytes` are parameters so the chunk-boundary and oversized-segment
/// paths are exercised with a few hundred bytes rather than a few gigabytes.
let foldMboxSegments
    (chunkSize: int)
    (maxMessageBytes: int)
    (stream: Stream)
    (readStartOffset: int64)
    (onSegment: 'state -> int64 -> byte[] -> bool -> 'state)
    (initial: 'state)
    : 'state =
    stream.Seek(readStartOffset, SeekOrigin.Begin) |> ignore

    let chunk = Array.zeroCreate<byte> (max 1 chunkSize)
    let mutable state = initial
    let mutable pending: byte[] = Array.empty
    let mutable pendingStart = readStartOffset
    // `pending` does NOT begin at a boundary: an oversized segment was skipped and the reader is
    // byte-scanning forward for the next "\n\nFrom " / "\n\r\nFrom ", keeping only a short carry.
    let mutable seeking = false
    // Still on the first bytes read: a resume from a watermark lands one past the previous
    // message's terminating newline, so `pending` can open with the blank line ("\n" or "\r\n")
    // whose "From " `segmentStartOffsets` cannot see the predecessor of. Trimmed once, here.
    let mutable atResumeSeam = readStartOffset > 0L
    let mutable atEof = false

    while not atEof do
        let read = stream.Read(chunk, 0, chunk.Length)

        if read = 0 then
            atEof <- true
            // A torn final message begins with "From " but was never terminated by the next
            // boundary; hand it over so the caller can decide it is torn. Boundary-less trailing
            // junk (never started with "From ") is dropped - the old whole-file split dropped it
            // too.
            if not seeking && pending.Length > 0 && startsWithFrom pending 0 then
                state <- onSegment state pendingStart pending true
        else
            // `chunk` is reused every read, so `pending` must never alias it: `Array.append` copies,
            // and `Array.copy` covers the first-chunk case where `incoming` IS `chunk`.
            let incoming = if read = chunk.Length then chunk else Array.sub chunk 0 read
            pending <- if pending.Length = 0 then Array.copy incoming else Array.append pending incoming

            if atResumeSeam && pending.Length >= fromLineBytes.Length + 2 then
                if pending.[0] = 10uy && startsWithFrom pending 1 then
                    pendingStart <- pendingStart + 1L
                    pending <- pending.[1..]
                elif pending.[0] = 13uy && pending.[1] = 10uy && startsWithFrom pending 2 then
                    pendingStart <- pendingStart + 2L
                    pending <- pending.[2..]

                atResumeSeam <- false

            let mutable progress = true

            while progress do
                progress <- false

                if seeking then
                    match segmentStartOffsets pending |> List.filter (fun o -> o > 0) with
                    | boundary :: _ ->
                        pendingStart <- pendingStart + int64 boundary
                        pending <- pending.[boundary..]
                        seeking <- false
                        progress <- true // re-run: `pending` now begins at a boundary
                    | [] when pending.Length > seekCarryBytes ->
                        // No boundary yet: discard all but a short tail so one split across the
                        // next read is still recognised.
                        let dropped = pending.Length - seekCarryBytes
                        pendingStart <- pendingStart + int64 dropped
                        pending <- pending.[dropped..]
                    | [] -> () // already down to the carry - wait for the next chunk
                else
                    match segmentStartOffsets pending with
                    | first :: _ when first > 0 ->
                        // Junk before the first message - only reachable when the file itself does
                        // not begin with "From ". Drop it and re-scan from the real first boundary.
                        pendingStart <- pendingStart + int64 first
                        pending <- pending.[first..]
                        progress <- true
                    | offsets ->
                        let bs = List.toArray offsets

                        if bs.Length >= 2 then
                            // Every boundary but the last closes a complete segment.
                            for i in 0 .. bs.Length - 2 do
                                state <- onSegment state (pendingStart + int64 bs.[i]) pending.[bs.[i] .. bs.[i + 1] - 1] false

                            let last = bs.[bs.Length - 1]
                            pendingStart <- pendingStart + int64 last
                            pending <- pending.[last..]
                        elif pending.Length > maxMessageBytes then
                            state <- onSegment state pendingStart pending false
                            pendingStart <- pendingStart + int64 pending.Length
                            pending <- Array.empty
                            seeking <- true
                            progress <- true
                        // else: 0 or 1 boundary, under the ceiling - wait for the next chunk.

    state

/// Strips the leading "From ..." envelope line, leaving standard RFC822 content MimeMessage.Load
/// can parse directly.
let private stripEnvelopeLine (bytes: byte[]) : byte[] =
    let text = latin1.GetString bytes
    let firstNewline = text.IndexOf('\n')

    if firstNewline < 0 then [||] else latin1.GetBytes(text.Substring(firstNewline + 1))

/// Decodes one attachment part's bytes. Only ever called for a part that HAS content - see
/// `parseMessage`'s filter, which is load-bearing rather than defensive: MimeKit leaves
/// `MimePart.Content` null for a part whose headers were parsed but whose body was not there,
/// and this line dereferences it.
let private toMailAttachment (part: MimePart) : MailAttachment =
    use content = new MemoryStream()
    part.Content.DecodeTo content

    let fileName =
        if String.IsNullOrWhiteSpace part.FileName then
            $"unnamed ({part.ContentType.MimeType})"
        else
            part.FileName

    {
        FileName = fileName
        DeclaredContentType = part.ContentType.MimeType
        Content = content.ToArray()
    }

/// Fully parses one RFC822 message (no envelope line) into the domain's MailMessage. Only
/// called for a message that passed the cutoff, or has no parseable Date at all.
let private parseMessage (rfc822Bytes: byte[]) (headerBlock: string) : MailMessage =
    use stream = new MemoryStream(rfc822Bytes)
    let message = MimeMessage.Load stream

    {
        SourceMessageId = messageIdOf headerBlock
        Sender = if isNull message.From then "" else message.From.ToString()
        Subject = if isNull message.Subject then "" else message.Subject
        ReceivedAt = message.Date.DateTime
        BodyText = message.TextBody |> Option.ofObj
        BodyHtml = message.HtmlBody |> Option.ofObj
        Attachments =
            message.Attachments
            |> Seq.choose (fun entity ->
                match entity with
                // A part whose headers are on disk but whose body is not has a NULL `Content`, and
                // that is the ordinary state of an mbox while Thunderbird is flushing a message
                // with an attachment: the part's headers go down before its base64 does. The
                // message's own headers and their blank line are already written by then, so
                // `readMboxFile`'s torn test (no header/body separator) passes it as complete and
                // it reaches this line - where `toMailAttachment` used to dereference the null and
                // throw a `NullReferenceException`. That is not a `FormatException`, so
                // `tryParseMessage` did not hold it, and not an `IOException` or an
                // `UnauthorizedAccessException`, so neither `readMboxFile` nor `readMaildirFolder`
                // did either: it escaped `readFolder`, `read` and the whole API call, from
                // signatures that say `Result<_, MailAccountError>`. Truncating one realistic
                // invoice email at each of its 666 byte offsets reaches this state at 64 of them.
                //
                // Skipped rather than reported as an empty attachment: there are no bytes behind
                // it, so handing the invoice pipeline a zero-byte "invoice.pdf" would turn "not
                // written yet" into "this PDF is corrupt". The message around it is entirely
                // readable and is still returned - the same instinct as the torn-message rule,
                // which keeps everything before the tear.
                | :? MimePart as part when not (isNull part.Content) -> Some(toMailAttachment part)
                | _ -> None)
            |> Seq.toList
    }

/// `parseMessage`, made total. MimeKit raises `FormatException` for content it cannot parse as
/// a message at all - "Failed to parse message headers." - and that is neither an `IOException`
/// nor an `UnauthorizedAccessException`, so it escaped `readMboxFile`'s and `readMaildirFolder`'s
/// handlers, out of `readFolder`, out of `read`, and out of the whole API call. A `Result`-
/// returning reader must not throw, and requirements.md -> "Reading safely while Thunderbird is
/// running" asks for the other folders to keep returning.
///
/// This is reachable from ordinary mail, not only from a corrupt file. mbox carries no length
/// header, so `segmentStartOffsets` has to guess a boundary from an unquoted "From " at the start
/// of a line preceded by a blank one - which a plain-text body signing off "From the accounts
/// team," satisfies exactly. The half after that false boundary begins with body text where
/// RFC822 headers should be, and one such line anywhere in one folder took down every folder of
/// the account. (A properly mbox-quoted ">From " never produces the split - FromQuotedBody.mbox
/// covers that - but nothing makes a sender quote it.)
///
/// Discarded rather than reported as a folder-level failure, and deliberately: the fragment is
/// the tail of a message that IS returned, so dropping it loses a body's last lines, whereas
/// failing the folder would lose every message in it - on a real INBOX, all of them, on every
/// scan. Same treatment a torn final message gets (requirements.md: "discard that partial message
/// and return everything before it, rather than failing the folder"), except that the offset still
/// advances past it: unlike a torn message, this will never become parseable.
let private tryParseMessage (rfc822Bytes: byte[]) (headerBlock: string) : MailMessage option =
    try
        Some(parseMessage rfc822Bytes headerBlock)
    with :? FormatException ->
        None

/// What one streamed segment contributed: a message to keep, or nothing, plus where the offset
/// should sit afterwards. `KeepNothingStopBefore` is a torn final message - the offset stays in
/// front of it so the whole thing is re-read once Thunderbird finishes writing it; every other
/// outcome advances past the segment.
type private SegmentOutcome =
    | KeepMessage of MailMessage
    | KeepNothingAdvance
    | KeepNothingStopBefore

/// One segment's cutoff-and-parse decision. A message whose Date cannot be found or parsed is
/// always kept - excluding it would be silent data loss with nothing on screen to show for it
/// (Q1.6). A segment with no header/body separator is torn when it is the last in the file, and
/// a false-boundary fragment (an unquoted "From " mid-body) otherwise - the first is re-read
/// later, the second never becomes parseable so the offset moves on.
let private classifySegment (cutoff: ScanCutoff) (isLast: bool) (segmentBytes: byte[]) : SegmentOutcome =
    if segmentBytes.LongLength > int64 MaxBufferableBytes then
        // Larger than a .NET string: a corrupt file or a mis-split, never a real message. Skip it
        // without trying to turn it into text.
        KeepNothingAdvance
    else
        match headerBlockAndRest (latin1.GetString segmentBytes) with
        | None when isLast -> KeepNothingStopBefore
        | None -> KeepNothingAdvance
        | Some(headerBlock, _) ->
            let shouldSkip =
                match tryParseHeaderDate headerBlock with
                | Some date -> date.DateTime < ScanCutoff.value cutoff
                | None -> false

            if shouldSkip then
                KeepNothingAdvance // skipped BEFORE the body is ever touched
            else
                match tryParseMessage (stripEnvelopeLine segmentBytes) headerBlock with
                | Some message -> KeepMessage message
                | None -> KeepNothingAdvance

/// Reads one mbox-format folder file, applying the cutoff, streaming it a chunk at a time so a
/// folder of any size stays in bounded memory (requirements.md -> "read it without loading the
/// whole folder into memory"). The file's own byte content is never modified - opened for read
/// only, with sharing that permits Thunderbird's own reads and writes.
let private readMboxFile (cutoff: ScanCutoff) (path: string) (fromOffset: int64) : Result<MailMessage list * int64, MailAccountError> =
    try
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let startOffset = normalizeStartOffset stream.Length fromOffset

        let messages = ResizeArray<MailMessage>()
        let mutable finalOffset = startOffset

        let onSegment () (segmentStart: int64) (segmentBytes: byte[]) (isLast: bool) : unit =
            match classifySegment cutoff isLast segmentBytes with
            | KeepMessage message ->
                messages.Add message
                finalOffset <- segmentStart + segmentBytes.LongLength
            | KeepNothingAdvance -> finalOffset <- segmentStart + segmentBytes.LongLength
            | KeepNothingStopBefore -> finalOffset <- segmentStart

        foldMboxSegments StreamChunkBytes MaxMessageBytes stream startOffset onSegment ()

        Ok(List.ofSeq messages, finalOffset)
    with
    | :? IOException as ex -> Error(MailFolderUnreadable(path, ex.Message))
    | :? UnauthorizedAccessException as ex -> Error(MailFolderUnreadable(path, ex.Message))
    | :? OutOfMemoryException ->
        Error(MailFolderUnreadable(path, "The folder holds a single message too large to read into memory."))

/// Reads one maildir-format folder's messages - synthetic-only (Q4.11), one message per file
/// under `cur` and `new`, each already a complete RFC822 message with no envelope line to strip.
let private readMaildirFolder (cutoff: ScanCutoff) (folderDirectory: string) : Result<MailMessage list, MailAccountError> =
    try
        let files =
            [ "cur"; "new" ]
            |> List.collect (fun sub ->
                let dir = Path.Combine(folderDirectory, sub)
                if Directory.Exists dir then Directory.GetFiles dir |> Array.toList else [])

        let messages =
            files
            |> List.choose (fun path ->
                use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                use reader = new StreamReader(stream, latin1)
                let text = reader.ReadToEnd()

                match headerBlockAndRest text with
                | None -> None
                | Some(headerBlock, _) ->
                    let shouldSkip =
                        match tryParseHeaderDate headerBlock with
                        | Some date -> date.DateTime < ScanCutoff.value cutoff
                        | None -> false

                    if shouldSkip then
                        None
                    else
                        // Same guard as the mbox branch - one file MimeKit cannot parse must not
                        // take the whole folder (and, through `read`, the whole account) with it.
                        tryParseMessage (latin1.GetBytes text) headerBlock)

        Ok messages
    with
    | :? IOException as ex -> Error(MailFolderUnreadable(folderDirectory, ex.Message))
    | :? UnauthorizedAccessException as ex -> Error(MailFolderUnreadable(folderDirectory, ex.Message))

/// Reads one folder, consulting and then updating its watermark. Exposed (not private) so the
/// locked-file and incremental-read scenarios are testable directly against one file, without
/// needing a full multi-folder account to be set up for what is fundamentally a per-folder
/// concern.
let readFolder
    (loadWatermark: LoadWatermark)
    (saveWatermark: SaveWatermark)
    (accountId: MailAccountId)
    (folder: MailFolder)
    (storeDirectory: string)
    (format: StoreFormat)
    (cutoff: ScanCutoff)
    : Result<MailMessage list, MailAccountError> =
    let fullPath = MailFolderEnumerator.resolvePath storeDirectory format folder.RelativePath

    match format with
    | Maildir -> readMaildirFolder cutoff fullPath
    | Mbox ->
        if not (File.Exists fullPath) then
            Ok []
        else
            result {
                let! existingWatermark = loadWatermark accountId folder.RelativePath
                let currentSize = FileInfo(fullPath).Length
                let currentModifiedAt = File.GetLastWriteTimeUtc fullPath

                let fromOffset =
                    match existingWatermark with
                    | Some wm when currentSize = wm.SizeBytes && currentModifiedAt = wm.ModifiedAt -> wm.OffsetReached
                    | Some wm when currentSize > wm.SizeBytes && currentModifiedAt >= wm.ModifiedAt -> wm.OffsetReached
                    | _ -> 0L // no watermark, a shrunk file, or an inconsistent mtime - full re-read

                let! messages, offsetReached = readMboxFile cutoff fullPath fromOffset

                do!
                    saveWatermark
                        accountId
                        folder.RelativePath
                        {
                            SizeBytes = currentSize
                            ModifiedAt = currentModifiedAt
                            OffsetReached = offsetReached
                        }

                return messages
            }

/// The account this id names, refusing one whose store directory is not on disk.
///
/// Discovery deliberately KEEPS a configured-but-missing account rather than dropping it
/// (requirements.md -> "Reading the profile"), and enumeration gives it no folders, because there
/// is no directory to enumerate. Both readers below then fold over an empty folder list and answer
/// "nothing here" - `Ok 0` and `Ok []` - which is exactly what a real, empty account answers. The
/// user asked how many messages their account holds, was told none, and nothing said the store had
/// gone: a silent wrong result, and the one this area's own error DU has carried the case for
/// (`StoreDirectoryMissing`) since the first commit without anything ever raising it.
///
/// Checked live rather than read off the account's `StoreDirectoryExists` flag: that flag is a
/// snapshot from the last scan, and the likely case is a drive unplugged or a folder deleted
/// BETWEEN scanning and counting, which a snapshot cannot see. The error says "does not exist" in
/// the present tense, so the present is what it has to be checked against.
let private accountWithReadableStore
    (lookupAccount: LookupAccount)
    (accountId: MailAccountId)
    : Result<DiscoveredMailAccount, MailAccountError> =
    result {
        let! accountOpt = lookupAccount accountId

        let! account =
            match accountOpt with
            | Some a -> Ok a
            | None -> Error(MailAccountNotFound accountId)

        if not (Directory.Exists account.StoreDirectory) then
            return! Error(StoreDirectoryMissing(accountId, account.StoreDirectory))
        else
            return account
    }

/// Reads every scannable folder of one account, smallest first (Decisions taken #10). A folder
/// that cannot be read is skipped rather than failing the whole call - the other folders still
/// return, matching requirements.md -> "Reading safely while Thunderbird is running".
let read
    (lookupAccount: LookupAccount)
    (loadWatermark: LoadWatermark)
    (saveWatermark: SaveWatermark)
    (accountId: MailAccountId)
    (cutoff: ScanCutoff)
    : Result<MailMessage list, MailAccountError> =
    result {
        let! account = accountWithReadableStore lookupAccount accountId

        let orderedFolders = account.Folders |> List.filter (fun f -> f.IsScannable) |> List.sortBy (fun f -> f.SizeBytes)

        let messages =
            orderedFolders
            |> List.collect (fun folder ->
                match readFolder loadWatermark saveWatermark accountId folder account.StoreDirectory account.StoreFormat cutoff with
                | Ok msgs -> msgs
                | Error _ -> [])

        return messages
    }

/// A headers-only pass: the count matches the number of complete messages in each scannable
/// folder, without parsing any body or attachment.
let countMessages (lookupAccount: LookupAccount) (accountId: MailAccountId) : Result<int, MailAccountError> =
    result {
        let! account = accountWithReadableStore lookupAccount accountId

        // A folder this cannot read contributes 0 and the rest still count. `with _ -> Ok 0` is the
        // last line of defence: an unexpected exception turns into "empty folder" rather than
        // failing the whole count. The streaming reader means a large folder no longer reaches it -
        // it used to answer `Ok 0` for a folder over `MaxBufferableBytes` (the whole span could not
        // be allocated, `Array.zeroCreate` threw, and this branch swallowed it), silently omitting
        // the user's largest folder from a confident total. That folder now streams like any other.
        let countOneFolder (folder: MailFolder) : Result<int, MailAccountError> =
            let fullPath = MailFolderEnumerator.resolvePath account.StoreDirectory account.StoreFormat folder.RelativePath

            match account.StoreFormat with
            | Maildir ->
                [ "cur"; "new" ]
                |> List.sumBy (fun sub ->
                    let dir = Path.Combine(fullPath, sub)
                    if Directory.Exists dir then Directory.GetFiles(dir).Length else 0)
                |> Ok
            | Mbox ->
                if not (File.Exists fullPath) then
                    Ok 0
                else
                    try
                        use stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

                        // A segment is a complete message iff it has a header/body separator: a
                        // torn final message and a false-boundary fragment both lack one, and
                        // `read` returns neither. An oversized non-message segment never counts.
                        let countSegment (count: int) (_: int64) (segmentBytes: byte[]) (_: bool) : int =
                            if segmentBytes.LongLength > int64 MaxBufferableBytes then count
                            elif (headerBlockAndRest (latin1.GetString segmentBytes)).IsSome then count + 1
                            else count

                        foldMboxSegments StreamChunkBytes MaxMessageBytes stream 0L countSegment 0
                        |> Ok
                    with _ ->
                        Ok 0

        return!
            account.Folders
            |> List.filter (fun f -> f.IsScannable)
            |> List.fold
                (fun running folder ->
                    running |> Result.bind (fun total -> countOneFolder folder |> Result.map (fun count -> total + count)))
                (Ok 0)
    }
