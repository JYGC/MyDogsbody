module MyDogsbody.Tests.Fixtures.DocumentFixtures

open System.IO
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing
open UglyToad.PdfPig.Writer
open UglyToad.PdfPig.Fonts.Standard14Fonts
open UglyToad.PdfPig.Core

/// Fixture documents for the four readers. Resolved by the source tree they live in, not the
/// process working directory - stable under `dotnet test`, `bin/Debug/net9.0` and an IDE runner,
/// the same arrangement ThunderbirdFixturePaths uses.
///
/// The binary formats that are cheap to hand-write are checked in under Documents/; the PDF and
/// .docx are built here at call time from real writers so there is no committed binary to drift
/// (the pattern PdfDocumentReaderTests already follows for PDFs). Everything is synthetic - no
/// real invoice content.
let private documentsRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "Documents"))

/// A checked-in fixture file by name.
let path (fileName: string) = Path.Combine(documentsRoot, fileName)

/// A legacy binary Word document (OLE2 compound file). Q1.12 chose .docx-only; this must produce
/// a listed unsupported-format problem naming "doc", never a silent skip (friction #8).
let legacyDoc = path "legacy.doc"

/// An .xlsx package. None of the measured spreadsheets were invoices; the dispatcher must name
/// the format so the question can be revisited from data (114 .xlsx against 1 .docx).
let spreadsheetXlsx = path "spreadsheet.xlsx"

/// A plain-text attachment: two label lines, a blank line, then a sentence - the blank line must
/// become a block boundary.
let plainTextAttachment = path "attachment.txt"

/// An HTML body whose label/value pairs sit in adjacent table cells. Block boundaries come from
/// the table structure, not from line breaks (Finding 5).
let tableBodyHtml = path "table-body.html"

/// Bytes of a real one-page PDF containing the given lines of text, top-down.
let pdfWithText (lines: string list) : byte[] =
    let builder = PdfDocumentBuilder()
    let page = builder.AddPage(595.0, 842.0)
    let font = builder.AddStandard14Font(Standard14Font.Helvetica)

    lines
    |> List.iteri (fun index line ->
        page.AddText(line, 12.0, PdfPoint(50.0, 780.0 - float index * 20.0), font) |> ignore)

    builder.Build()

/// Bytes of a one-page PDF with no text layer at all - a page and nothing on it, standing in for
/// a scanned image. The reader must report DocumentHasNoTextLayer, not attempt OCR.
let pdfWithNoTextLayer () : byte[] =
    let builder = PdfDocumentBuilder()
    builder.AddPage(595.0, 842.0) |> ignore
    builder.Build()

/// Bytes that carry a .pdf name but are not a PDF - the file that cannot be opened at all
/// (3.7% of the measured PDFs).
let unopenablePdf () : byte[] =
    System.Text.Encoding.UTF8.GetBytes "%PDF-1.4 this file is truncated and unreadable"

/// Bytes of a real .docx whose body is the given paragraphs, one Word paragraph each.
let docxWithParagraphs (paragraphs: string list) : byte[] =
    use stream = new MemoryStream()

    (
        use doc =
            WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document)

        let main = doc.AddMainDocumentPart()
        main.Document <- Document(Body())
        let body = main.Document.Body

        for text in paragraphs do
            body.AppendChild(Paragraph(Run(Text(text)))) |> ignore

        main.Document.Save()
    )

    stream.ToArray()
