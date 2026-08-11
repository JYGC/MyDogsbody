namespace MyDogsbody.Domain.Documents

/// Where a document lives. Whether anything is actually there is I/O, so it is not checked here -
/// the adapter finds out and reports DocumentUnreadable.
type DocumentPath = private DocumentPath of string

module DocumentPath =

    let create (value: string) : Result<DocumentPath, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error "Document path must not be empty."
        else
            Ok (DocumentPath value)

    let value (DocumentPath path) = path

/// A word and where it sits on the page. Coordinates grow upwards, as PDFs measure them.
type Word =
    {
        Text: string
        Bottom: float
        Left: float
    }

type DocumentContent =
    {
        Words: Word list
    }

type DocumentError =
    | DocumentPathInvalid of reason: string
    | DocumentUnreadable of message: string

/// The one capability this area needs from the outside world. PdfPig satisfies it today; an
/// adapter for a different format would satisfy the same type without the workflow noticing.
type ReadDocumentContent = DocumentPath -> Result<DocumentContent, DocumentError>

/// Which shape a scanned message part arrived in. A record type also named Word already exists
/// in this namespace (a PDF word with coordinates) - it and this case coexist without collision
/// because F# keeps types and union-case values in separate namespaces.
type DocumentFormat = Pdf | Word | PlainText | EmailBody

/// A line of extracted text and the block it came from.
///
/// BlockIndex is what makes Finding 4's join rule expressible: a wrapped continuation may be
/// joined to its predecessor WITHIN a block, and never across one, because LinesAfterLabel
/// depends on the structure a block boundary marks. A reader assigns it - a paragraph, a table
/// cell, a PDF text block. Plain text splits on blank lines.
type TextLine = { Text: string; BlockIndex: int }
