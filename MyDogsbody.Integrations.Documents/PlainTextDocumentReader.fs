/// The plain-text adapter: satisfies the domain's ReadDocumentText for DocumentFormat.PlainText.
module MyDogsbody.Integrations.Documents.PlainTextDocumentReader

open System
open System.IO
open System.Text
open MyDogsbody.Domain.Documents

/// Splits decoded text into lines and tags each with a block index. A run of one or more blank
/// lines is a single block boundary and the blank lines themselves are dropped - Finding 4:
/// "Drop empty lines before applying line offsets, so LinesAfterLabel(label, 1) means 'the next
/// line with content'."
let private toBlocks (text: string) : TextLine list =
    let rawLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')

    rawLines
    |> Array.fold
        (fun (acc, block, sawContentInBlock, pendingBoundary) rawLine ->
            let line = rawLine.Trim()

            if line = "" then
                // A blank line: mark that the NEXT content line starts a new block, but only if
                // this block already has content (so leading blank lines don't count).
                (acc, block, sawContentInBlock, sawContentInBlock)
            else
                let block = if pendingBoundary then block + 1 else block
                ({ Text = line; BlockIndex = block } :: acc, block, true, false))
        ([], 0, false, false)
    |> fun (acc, _, _, _) -> List.rev acc

/// Returns DocumentError directly - see the invoice-extraction design.md -> "Action names".
let readText (source: DocumentSource) : Result<TextLine list, DocumentError> =
    if isNull source.Content || source.Content.Length = 0 then
        Error(DocumentUnreadable "The document is empty.")
    else
        try
            use stream = new MemoryStream(source.Content)
            // detectEncodingFromByteOrderMarks honours a BOM; UTF-8 otherwise, which is what the
            // measured mailbox's text parts use.
            use reader = new StreamReader(stream, Encoding.UTF8, true)
            Ok(toBlocks (reader.ReadToEnd()))
        with ex ->
            Error(DocumentUnreadable ex.Message)
