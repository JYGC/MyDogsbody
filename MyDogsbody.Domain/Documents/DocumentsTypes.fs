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
