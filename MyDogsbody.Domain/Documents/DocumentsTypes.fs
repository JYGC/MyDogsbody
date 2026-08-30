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
    /// The attachment's format has no reader. Carries the format (a file extension, lower-cased,
    /// no dot) so the scan problem can say WHICH format arrived - the measured mailbox holds 114
    /// .xlsx against 1 .docx, and silence would look identical to "this supplier sends nothing".
    | DocumentFormatUnsupported of format: string
    /// The file opened but carries no extractable text - a scanned image. Distinct from
    /// DocumentUnreadable on purpose: 1.6% of the measured PDFs are this and 3.7% will not open
    /// at all, and only one of those is a candidate for OCR later. No OCR is done here (94.7%
    /// have a text layer).
    | DocumentHasNoTextLayer

/// The one capability this area needs from the outside world. PdfPig satisfies it today; an
/// adapter for a different format would satisfy the same type without the workflow noticing.
type ReadDocumentContent = DocumentPath -> Result<DocumentContent, DocumentError>

/// Which shape a scanned message part arrived in. A record type also named Word already exists
/// in this namespace (a PDF word with coordinates) - it and this case coexist without collision
/// because F# keeps types and union-case values in separate namespaces.
type DocumentFormat = Pdf | Word | PlainText | EmailBody

module DocumentFormat =

    /// The reader for an attachment is chosen by its FILENAME EXTENSION, never by the declared
    /// content type - 155 of 644 measured PDFs declare application/octet-stream and 4 declare
    /// application/.pdf, so dispatching on the declared type misroutes a quarter of them. This
    /// function does not take a content type at all.
    ///
    /// EmailBody has no filename, so it is never returned here - ScanMessageWorkflow sets it
    /// directly for a message body. On an unknown or absent extension the lower-cased extension
    /// (or "none") comes back as the Error, so the scan problem can name the format.
    let ofFileName (fileName: string) : Result<DocumentFormat, string> =
        let extension =
            match fileName with
            | null -> ""
            | name -> System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant()

        match extension with
        | "pdf" -> Ok Pdf
        | "docx"
        | "doc" -> Ok Word
        | "txt"
        | "text" -> Ok PlainText
        | "" -> Error "none"
        | other -> Error other

/// A line of extracted text and the block it came from.
///
/// BlockIndex is what makes Finding 4's join rule expressible: a wrapped continuation may be
/// joined to its predecessor WITHIN a block, and never across one, because LinesAfterLabel
/// depends on the structure a block boundary marks. A reader assigns it - a paragraph, a table
/// cell, a PDF text block. Plain text splits on blank lines.
type TextLine = { Text: string; BlockIndex: int }

/// An attachment or a message body, as bytes plus enough to route it.
///
/// Bytes, not a path (Q1.11): the attachment lives inside a 2.5 GB mbox and should not have to be
/// spilled to a temp file - and cleaned up, and kept out of a backup - to be read. Not a
/// constrained type: there is nothing to validate here that a reader does not find out for
/// itself. Name is the filename for an attachment (the dispatcher reads its extension) and a
/// synthetic label for a body.
///
/// Format is decided by the composition root FROM THE FILENAME EXTENSION, never from a declared
/// content type - 155 of 644 measured PDFs declare application/octet-stream and 4 declare
/// application/.pdf, so dispatching on the declared type misroutes a quarter of them.
type DocumentSource = { Format: DocumentFormat; Name: string; Content: byte[] }

/// Read a document's text, one TextLine per line, each tagged with the block it came from.
///
/// One type for all four formats: the composition root binds one reader per format and
/// dispatches on Format, so every workflow sees a single function.
///
/// This does NOT replace ReadDocumentContent, which takes a DocumentPath and returns
/// coordinate-bearing Words for a future SameRowAsLabel rule. Both live here, both are satisfied
/// by PdfDocumentReader, and each owes its own contract suite (friction #7). DocumentSource and
/// DocumentPath are different enough types that the composition root cannot bind the wrong one
/// silently.
type ReadDocumentText = DocumentSource -> Result<TextLine list, DocumentError>
