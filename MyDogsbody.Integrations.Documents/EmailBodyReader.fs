/// The email-body adapter: satisfies the domain's ReadDocumentText for DocumentFormat.EmailBody.
///
/// The measured mailbox (Finding 5) showed that where a body matters at all the HTML alternative
/// is the better source, because its table structure keeps a label and its value adjacent while
/// the plain-text alternative has already wrapped them apart. ScanMessageWorkflow hands this
/// reader the HTML alternative when the message has one; a plain-only body is routed to
/// PlainTextDocumentReader instead.
module MyDogsbody.Integrations.Documents.EmailBodyReader

open System
open System.Text
open System.Text.RegularExpressions
open HtmlAgilityPack
open MyDogsbody.Domain.Documents

/// Tags that end the current line and begin a new block. A table row is a block; each cell in it
/// is a line, so "Invoice Number" and its value land adjacent in one block and
/// LinesAfterLabel(label, 1) resolves.
let private blockTags =
    set
        [ "p"; "div"; "tr"; "li"; "h1"; "h2"; "h3"; "h4"; "h5"; "h6"
          "blockquote"; "table"; "ul"; "ol"; "section"; "article"; "header"; "footer"; "pre" ]

/// Tags whose text is a line of its own inside the current block (table cells).
let private cellTags = set [ "td"; "th" ]

let private whitespace = Regex(@"\s+", RegexOptions.Compiled)

let private normalize (text: string) : string =
    whitespace.Replace(HtmlEntity.DeEntitize text, " ").Trim()

type private Walk =
    { Lines: (int * string) list
      Block: int
      Buffer: StringBuilder }

let private flush (walk: Walk) : Walk =
    let line = normalize (walk.Buffer.ToString())
    walk.Buffer.Clear() |> ignore

    if line = "" then
        walk
    else
        { walk with Lines = (walk.Block, line) :: walk.Lines }

let rec private visit (walk: Walk) (node: HtmlNode) : Walk =
    match node.NodeType with
    | HtmlNodeType.Text ->
        walk.Buffer.Append(' ').Append(node.InnerText) |> ignore
        walk
    | HtmlNodeType.Comment -> walk
    | _ ->
        let tag = node.Name.ToLowerInvariant()

        if tag = "br" then
            flush walk
        elif tag = "script" || tag = "style" || tag = "head" then
            walk
        elif Set.contains tag cellTags then
            let walk = node.ChildNodes |> Seq.fold visit walk
            flush walk
        elif Set.contains tag blockTags then
            let walk = flush walk
            let walk = { walk with Block = walk.Block + 1 }
            let walk = node.ChildNodes |> Seq.fold visit walk
            let walk = flush walk
            { walk with Block = walk.Block + 1 }
        else
            node.ChildNodes |> Seq.fold visit walk

/// Renumbers the blocks that actually carry lines to 0, 1, 2 … in document order.
let private renumber (lines: (int * string) list) : TextLine list =
    let ordered = List.rev lines

    let mapping =
        ordered
        |> List.map fst
        |> List.distinct
        |> List.mapi (fun index block -> block, index)
        |> Map.ofList

    ordered |> List.map (fun (block, text) -> { Text = text; BlockIndex = mapping.[block] })

/// Returns DocumentError directly - see the invoice-extraction design.md -> "Action names".
let readText (source: DocumentSource) : Result<TextLine list, DocumentError> =
    if isNull source.Content || source.Content.Length = 0 then
        Error(DocumentUnreadable "The document is empty.")
    else
        try
            let html = Encoding.UTF8.GetString source.Content
            let document = HtmlDocument()
            document.LoadHtml html

            let root =
                document.DocumentNode.SelectSingleNode("//body")
                |> Option.ofObj
                |> Option.defaultValue document.DocumentNode

            let walk =
                root.ChildNodes
                |> Seq.fold visit { Lines = []; Block = 0; Buffer = StringBuilder() }
                |> flush

            Ok(renumber walk.Lines)
        with ex ->
            Error(DocumentUnreadable ex.Message)
