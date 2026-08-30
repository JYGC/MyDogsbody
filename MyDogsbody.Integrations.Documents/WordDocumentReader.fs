/// The .docx adapter: satisfies the domain's ReadDocumentText for DocumentFormat.Word.
///
/// DocumentFormat.OpenXml lives here and nowhere else. Q1.12 chose .docx-only - NPOI never
/// enters the solution, and a legacy binary .doc is reported as an unsupported format rather
/// than skipped.
module MyDogsbody.Integrations.Documents.WordDocumentReader

open System
open System.IO
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing
open MyDogsbody.Domain.Documents

/// Returns DocumentError directly - see the invoice-extraction design.md -> "Action names".
/// Every outcome here is a domain fact the scan lists as a problem.
let readText (source: DocumentSource) : Result<TextLine list, DocumentError> =
    let name = if isNull source.Name then "" else source.Name

    if
        name.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)
        && not (name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
    then
        // friction #8: name the format, never skip silently.
        Error(DocumentFormatUnsupported "doc")
    elif isNull source.Content || source.Content.Length = 0 then
        Error(DocumentUnreadable "The document is empty.")
    else
        try
            use stream = new MemoryStream(source.Content)
            use document = WordprocessingDocument.Open(stream, false)

            match Option.ofObj document.MainDocumentPart |> Option.bind (fun p -> Option.ofObj p.Document) with
            | None -> Error(DocumentUnreadable "The document has no main part.")
            | Some root ->
                // One paragraph is one block: a wrapped line inside a paragraph belongs with its
                // predecessor, a paragraph break does not (Finding 4).
                let lines =
                    root.Descendants<Paragraph>()
                    |> Seq.mapi (fun index paragraph -> index, paragraph.InnerText)
                    |> Seq.filter (fun (_, text) -> not (String.IsNullOrWhiteSpace text))
                    |> Seq.map (fun (index, text) -> { Text = text; BlockIndex = index })
                    |> Seq.toList

                Ok lines
        with ex ->
            Error(DocumentUnreadable ex.Message)
