/// The plain-text adapter: satisfies the domain's ReadDocumentText for DocumentFormat.PlainText.
module MyDogsbody.Integrations.Documents.PlainTextDocumentReader

open System
open System.IO
open System.Text
open MyDogsbody.Domain.Documents

/// A run of one or more blank lines is a single block boundary and the blank lines themselves are
/// dropped - Finding 4: "Drop empty lines before applying line offsets, so
/// LinesAfterLabel(label, 1) means 'the next line with content'."
let private splitDecodedTextIntoLinesTaggedWithABlockIndex (text: string) : TextLine list =
    let rawLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')

    rawLines
    |> Array.fold
        (fun (reversedTaggedLines, block, sawContentInBlock, pendingBoundary) rawLine ->
            let line = rawLine.Trim()

            if line = "" then
                // A blank line: mark that the NEXT content line starts a new block, but only if
                // this block already has content (so leading blank lines don't count).
                (reversedTaggedLines, block, sawContentInBlock, sawContentInBlock)
            else
                let block = if pendingBoundary then block + 1 else block
                ({ Text = line; BlockIndex = block } :: reversedTaggedLines, block, true, false))
        ([], 0, false, false)
    |> fun (reversedTaggedLines, _, _, _) -> List.rev reversedTaggedLines

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
            Ok(splitDecodedTextIntoLinesTaggedWithABlockIndex (reader.ReadToEnd()))
        with caughtException ->
            Error(DocumentUnreadable caughtException.Message)
