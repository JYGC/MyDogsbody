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
let private headerBlockAndRest (text: string) : (string * string) option =
    let idx = text.IndexOf("\n\n", StringComparison.Ordinal)

    if idx < 0 then
        None
    else
        Some(text.Substring(0, idx), text.Substring(idx + 2))

/// Splits raw mbox bytes into per-message byte ranges at "From " envelope lines - the only
/// place this file looks for a boundary, independent of MIME structure entirely, so one
/// message's malformed or unterminated multipart body can never swallow the messages that
/// follow it in the same file. A boundary line is the first line of the buffer, or any line
/// immediately preceded by a blank one - a properly mbox-quoted body never produces a false
/// match (FromQuotedBody.mbox exercises exactly this).
let private splitIntoMessages (bytes: byte[]) : byte[] list =
    if bytes.Length = 0 then
        []
    else
        let text = latin1.GetString bytes
        let lines = text.Split('\n')

        let isBoundary i =
            lines.[i].StartsWith("From ", StringComparison.Ordinal)
            && (i = 0 || lines.[i - 1].TrimEnd('\r') = "")

        let boundaryLineIndexes = [| 0 .. lines.Length - 1 |] |> Array.filter isBoundary

        if boundaryLineIndexes.Length = 0 then
            []
        else
            boundaryLineIndexes
            |> Array.mapi (fun idx startLine ->
                let endLineExclusive =
                    if idx + 1 < boundaryLineIndexes.Length then
                        boundaryLineIndexes.[idx + 1]
                    else
                        lines.Length

                let segmentLines = lines.[startLine .. endLineExclusive - 1]
                latin1.GetBytes(String.Join("\n", segmentLines)))
            |> Array.toList

/// Strips the leading "From ..." envelope line, leaving standard RFC822 content MimeMessage.Load
/// can parse directly.
let private stripEnvelopeLine (bytes: byte[]) : byte[] =
    let text = latin1.GetString bytes
    let firstNewline = text.IndexOf('\n')

    if firstNewline < 0 then [||] else latin1.GetBytes(text.Substring(firstNewline + 1))

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
                | :? MimePart as part -> Some(toMailAttachment part)
                | _ -> None)
            |> Seq.toList
    }

/// One segment's cutoff decision plus, only when it is worth keeping, the fully parsed message.
/// A message whose Date cannot be found or parsed is always kept - excluding it would be silent
/// data loss with nothing on screen to show for it (Q1.6).
let private processSegment (cutoff: ScanCutoff) (segmentBytes: byte[]) : MailMessage option =
    let segmentText = latin1.GetString segmentBytes

    match headerBlockAndRest segmentText with
    | None -> None // torn: no header/body separator at all before EOF
    | Some(headerBlock, _) ->
        let shouldSkip =
            match tryParseHeaderDate headerBlock with
            | Some date -> date.DateTime < ScanCutoff.value cutoff
            | None -> false

        if shouldSkip then
            None // skipped BEFORE the body is ever touched - see splitIntoMessages's comment
        else
            let rfc822Bytes = stripEnvelopeLine segmentBytes
            Some(parseMessage rfc822Bytes headerBlock)

/// Reads one mbox-format folder file in full, applying the cutoff. The file's own byte content
/// is never modified - opened for read only, with sharing that permits Thunderbird's own reads
/// and writes.
let private readMboxFile (cutoff: ScanCutoff) (path: string) (fromOffset: int64) : Result<MailMessage list * int64, MailAccountError> =
    try
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let totalLength = stream.Length
        stream.Seek(fromOffset, SeekOrigin.Begin) |> ignore

        let remainingLength = int (totalLength - fromOffset)
        let buffer = Array.zeroCreate<byte> remainingLength
        let mutable readSoFar = 0

        while readSoFar < remainingLength do
            let read = stream.Read(buffer, readSoFar, remainingLength - readSoFar)
            if read = 0 then
                readSoFar <- remainingLength // EOF reached early; treat what we have as final
            else
                readSoFar <- readSoFar + read

        let segments = splitIntoMessages buffer

        match segments with
        | [] -> Ok([], fromOffset)
        | segments ->
            let lastIndex = segments.Length - 1

            let mutable offsetWithinBuffer = 0
            let mutable finalOffset = fromOffset
            let messages = ResizeArray<MailMessage>()

            segments
            |> List.iteri (fun i segment ->
                let isLast = i = lastIndex
                let segmentText = latin1.GetString segment

                let isTorn = isLast && (headerBlockAndRest segmentText).IsNone

                if isTorn then
                    () // discarded; finalOffset stops BEFORE this segment, so it is re-tried later
                else
                    match processSegment cutoff segment with
                    | Some message -> messages.Add message
                    | None -> ()

                    offsetWithinBuffer <- offsetWithinBuffer + segment.Length
                    finalOffset <- fromOffset + int64 offsetWithinBuffer)

            Ok(List.ofSeq messages, finalOffset)
    with
    | :? IOException as ex -> Error(MailFolderUnreadable(path, ex.Message))
    | :? UnauthorizedAccessException as ex -> Error(MailFolderUnreadable(path, ex.Message))

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
                        Some(parseMessage (latin1.GetBytes text) headerBlock))

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
        let! accountOpt = lookupAccount accountId

        let! account =
            match accountOpt with
            | Some a -> Ok a
            | None -> Error(MailAccountNotFound accountId)

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
        let! accountOpt = lookupAccount accountId

        let! account =
            match accountOpt with
            | Some a -> Ok a
            | None -> Error(MailAccountNotFound accountId)

        let countOneFolder (folder: MailFolder) : int =
            let fullPath = MailFolderEnumerator.resolvePath account.StoreDirectory account.StoreFormat folder.RelativePath

            match account.StoreFormat with
            | Maildir ->
                [ "cur"; "new" ]
                |> List.sumBy (fun sub ->
                    let dir = Path.Combine(fullPath, sub)
                    if Directory.Exists dir then Directory.GetFiles(dir).Length else 0)
            | Mbox ->
                if not (File.Exists fullPath) then
                    0
                else
                    try
                        use stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        let buffer = Array.zeroCreate<byte> (int stream.Length)
                        let mutable readSoFar = 0

                        while readSoFar < buffer.Length do
                            let read = stream.Read(buffer, readSoFar, buffer.Length - readSoFar)
                            if read = 0 then readSoFar <- buffer.Length else readSoFar <- readSoFar + read

                        let segments = splitIntoMessages buffer

                        segments
                        |> List.mapi (fun i segment -> i = segments.Length - 1, latin1.GetString segment)
                        |> List.filter (fun (isLast, text) -> not (isLast && (headerBlockAndRest text).IsNone))
                        |> List.length
                    with _ ->
                        0

        return account.Folders |> List.filter (fun f -> f.IsScannable) |> List.sumBy countOneFolder
    }
