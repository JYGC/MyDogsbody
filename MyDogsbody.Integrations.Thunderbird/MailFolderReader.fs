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

/// Integration-internal - the domain only ever sees `ClearWatermarks`.
type FolderWatermark =
    {
        SizeBytes: int64
        ModifiedAt: DateTime
        OffsetReached: int64
        /// The cutoff `OffsetReached` was reached under. `DateTime.MinValue` means "not recorded"
        /// - see `resumeOffset`, which treats that as unknown rather than as "no cutoff".
        CutoffReached: DateTime
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
    let dateMatch = dateHeaderPattern.Match headerBlock

    if not dateMatch.Success then
        None
    else
        match DateUtils.TryParse(dateMatch.Groups.["value"].Value) with
        | true, date -> Some date
        | false, _ -> None

/// A stable identifier for a message with no Message-ID header - a hash of the header block
/// text, not a byte offset, so it survives a compaction that removes a preceding message
/// (design.md -> Decisions taken #6).
let private synthesizeMessageId (headerBlock: string) : string =
    let bytes = SHA256.HashData(latin1.GetBytes headerBlock)
    "synthesized:" + Convert.ToHexString(bytes).ToLowerInvariant()

let private messageIdOf (headerBlock: string) : string =
    let messageIdMatch = messageIdHeaderPattern.Match headerBlock
    if messageIdMatch.Success && not (String.IsNullOrWhiteSpace messageIdMatch.Groups.["value"].Value) then
        messageIdMatch.Groups.["value"].Value.Trim()
    else
        synthesizeMessageId headerBlock

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Integrations.Thunderbird.md - MailFolderReader.fs: headerBlockAndTheTextAfterTheBlankLineThatEndsItOrNoneWhenThereIsNoBlankLine
let private headerBlockAndTheTextAfterTheBlankLineThatEndsItOrNoneWhenThereIsNoBlankLine
    (text: string)
    : (string * string) option =
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

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Integrations.Thunderbird.md - MailFolderReader.fs: resumeOffset
let resumeOffset
    (cutoffAt: DateTime)
    (currentSize: int64)
    (currentModifiedAt: DateTime)
    (existingWatermark: FolderWatermark option)
    : int64 =
    match existingWatermark with
    // Written before the cutoff was recorded: unknown, not "no cutoff". One full re-read, after
    // which the stored value is real.
    | Some watermark when watermark.CutoffReached = DateTime.MinValue -> 0L
    | Some watermark when cutoffAt < watermark.CutoffReached -> 0L // the window widened
    | Some watermark when currentSize = watermark.SizeBytes && currentModifiedAt = watermark.ModifiedAt -> watermark.OffsetReached
    | Some watermark when currentSize > watermark.SizeBytes && currentModifiedAt >= watermark.ModifiedAt -> watermark.OffsetReached
    | _ -> 0L // no watermark, a shrunk file, or an inconsistent mtime - full re-read

let private asciiBytesOfFromFollowedByASpace = [| 70uy; 114uy; 111uy; 109uy; 32uy |]

let private startsWithFrom (bytes: byte[]) (at: int) : bool =
    at + asciiBytesOfFromFollowedByASpace.Length <= bytes.Length
    && Array.forall2 (=) asciiBytesOfFromFollowedByASpace bytes.[at .. at + asciiBytesOfFromFollowedByASpace.Length - 1]

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Integrations.Thunderbird.md - MailFolderReader.fs: segmentStartOffsets
let segmentStartOffsets (bytes: byte[]) : int list =
    let offsets = ResizeArray<int>()

    if startsWithFrom bytes 0 then
        offsets.Add 0

    // A boundary "From " begins at index+1, where bytes.[index] = '\n' and the line before it was
    // blank: bytes.[index-1] = '\n', or bytes.[index-1] = '\r' with bytes.[index-2] = '\n'.
    for index in 1 .. bytes.Length - 2 do
        if bytes.[index] = 10uy && startsWithFrom bytes (index + 1) then
            let blankLineBefore =
                bytes.[index - 1] = 10uy || (bytes.[index - 1] = 13uy && index >= 2 && bytes.[index - 2] = 10uy)

            if blankLineBefore then
                offsets.Add(index + 1)

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

            if atResumeSeam && pending.Length >= asciiBytesOfFromFollowedByASpace.Length + 2 then
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
                    match segmentStartOffsets pending |> List.filter (fun boundaryOffset -> boundaryOffset > 0) with
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
                        let boundaryOffsets = List.toArray offsets

                        if boundaryOffsets.Length >= 2 then
                            // Every boundary but the last closes a complete segment.
                            for index in 0 .. boundaryOffsets.Length - 2 do
                                state <-
                                    onSegment
                                        state
                                        (pendingStart + int64 boundaryOffsets.[index])
                                        pending.[boundaryOffsets.[index] .. boundaryOffsets.[index + 1] - 1]
                                        false

                            let last = boundaryOffsets.[boundaryOffsets.Length - 1]
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

/// Only ever called for a part that HAS content - see `parseMessage`'s filter, which is
/// load-bearing rather than defensive: MimeKit leaves `MimePart.Content` null for a part whose
/// headers were parsed but whose body was not there, and this line dereferences it.
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

/// Only called for a message that passed the cutoff, or has no parseable Date at all.
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
                // Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Integrations.Thunderbird.md - MailFolderReader.fs: | :? MimePart as part when not (isNull part.Content) -> Some
                | :? MimePart as part when not (isNull part.Content) -> Some(toMailAttachment part)
                | _ -> None)
            |> Seq.toList
    }

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Integrations.Thunderbird.md - MailFolderReader.fs: tryParseMessage
let private tryParseMessage (rfc822Bytes: byte[]) (headerBlock: string) : MailMessage option =
    try
        Some(parseMessage rfc822Bytes headerBlock)
    with :? FormatException ->
        None

/// `KeepNothingStopBefore` is a torn final message - the offset stays in front of it so the whole
/// thing is re-read once Thunderbird finishes writing it; every other outcome advances past the
/// segment.
type private SegmentOutcome =
    | KeepMessage of MailMessage
    | KeepNothingAdvance
    | KeepNothingStopBefore

/// A message whose Date cannot be found or parsed is always kept - excluding it would be silent
/// data loss with nothing on screen to show for it (Q1.6). A segment with no header/body separator
/// is torn when it is the last in the file, and a false-boundary fragment (an unquoted "From "
/// mid-body) otherwise - the first is re-read later, the second never becomes parseable so the
/// offset moves on.
let private classifySegment (cutoff: ScanCutoff) (isLast: bool) (segmentBytes: byte[]) : SegmentOutcome =
    if segmentBytes.LongLength > int64 MaxBufferableBytes then
        // Larger than a .NET string: a corrupt file or a mis-split, never a real message. Skip it
        // without trying to turn it into text.
        KeepNothingAdvance
    else
        match headerBlockAndTheTextAfterTheBlankLineThatEndsItOrNoneWhenThereIsNoBlankLine (latin1.GetString segmentBytes) with
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
    | :? IOException as caughtIOException -> Error(MailFolderUnreadable(path, caughtIOException.Message))
    | :? UnauthorizedAccessException as caughtUnauthorizedAccessException ->
        Error(MailFolderUnreadable(path, caughtUnauthorizedAccessException.Message))
    | :? OutOfMemoryException ->
        Error(MailFolderUnreadable(path, "The folder holds a single message too large to read into memory."))

/// Reads one maildir-format folder's messages - synthetic-only (Q4.11), one message per file
/// under `cur` and `new`, each already a complete RFC822 message with no envelope line to strip.
let private readMaildirFolder (cutoff: ScanCutoff) (folderDirectory: string) : Result<MailMessage list, MailAccountError> =
    try
        let files =
            [ "cur"; "new" ]
            |> List.collect (fun subdirectoryName ->
                let subdirectoryPath = Path.Combine(folderDirectory, subdirectoryName)
                if Directory.Exists subdirectoryPath then Directory.GetFiles subdirectoryPath |> Array.toList else [])

        let messages =
            files
            |> List.choose (fun path ->
                use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                use reader = new StreamReader(stream, latin1)
                let text = reader.ReadToEnd()

                match headerBlockAndTheTextAfterTheBlankLineThatEndsItOrNoneWhenThereIsNoBlankLine text with
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
    | :? IOException as caughtIOException -> Error(MailFolderUnreadable(folderDirectory, caughtIOException.Message))
    | :? UnauthorizedAccessException as caughtUnauthorizedAccessException ->
        Error(MailFolderUnreadable(folderDirectory, caughtUnauthorizedAccessException.Message))

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
                let cutoffAt = ScanCutoff.value cutoff

                let fromOffset = resumeOffset cutoffAt currentSize currentModifiedAt existingWatermark

                let! messages, offsetReached = readMboxFile cutoff fullPath fromOffset

                do!
                    saveWatermark
                        accountId
                        folder.RelativePath
                        {
                            SizeBytes = currentSize
                            ModifiedAt = currentModifiedAt
                            OffsetReached = offsetReached
                            CutoffReached = cutoffAt
                        }

                return messages
            }

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Integrations.Thunderbird.md - MailFolderReader.fs: accountWithReadableStore
let private accountWithReadableStore
    (lookupAccount: LookupAccount)
    (accountId: MailAccountId)
    : Result<DiscoveredMailAccount, MailAccountError> =
    result {
        let! accountOpt = lookupAccount accountId

        let! account =
            match accountOpt with
            | Some discoveredAccount -> Ok discoveredAccount
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

        let orderedFolders =
            account.Folders |> List.filter (fun folder -> folder.IsScannable) |> List.sortBy (fun folder -> folder.SizeBytes)

        let messages =
            orderedFolders
            |> List.collect (fun folder ->
                match readFolder loadWatermark saveWatermark accountId folder account.StoreDirectory account.StoreFormat cutoff with
                | Ok folderMessages -> folderMessages
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
                |> List.sumBy (fun subdirectoryName ->
                    let subdirectoryPath = Path.Combine(fullPath, subdirectoryName)
                    if Directory.Exists subdirectoryPath then Directory.GetFiles(subdirectoryPath).Length else 0)
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
                            elif
                                latin1.GetString segmentBytes
                                |> headerBlockAndTheTextAfterTheBlankLineThatEndsItOrNoneWhenThereIsNoBlankLine
                                |> Option.isSome
                            then
                                count + 1
                            else count

                        foldMboxSegments StreamChunkBytes MaxMessageBytes stream 0L countSegment 0
                        |> Ok
                    with _ ->
                        Ok 0

        return!
            account.Folders
            |> List.filter (fun folder -> folder.IsScannable)
            |> List.fold
                (fun running folder ->
                    running |> Result.bind (fun total -> countOneFolder folder |> Result.map (fun count -> total + count)))
                (Ok 0)
    }
