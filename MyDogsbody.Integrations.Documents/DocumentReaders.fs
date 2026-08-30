/// The composition-root binding for the domain's ReadDocumentText: one function that dispatches
/// a DocumentSource to the reader for its format. The four readers are partially applied here in
/// Startup.fs; the domain sees a single ReadDocumentText value.
module MyDogsbody.Integrations.Documents.DocumentReaders

open MyDogsbody.Domain.Documents

/// Routes on DocumentSource.Format. The Format was decided upstream from the filename extension
/// (DocumentFormat.ofFileName) - this function never looks at a content type.
let dispatch
    (readPdf: ReadDocumentText)
    (readWord: ReadDocumentText)
    (readPlainText: ReadDocumentText)
    (readEmailBody: ReadDocumentText)
    : ReadDocumentText =
    fun source ->
        match source.Format with
        | Pdf -> readPdf source
        | Word -> readWord source
        | PlainText -> readPlainText source
        | EmailBody -> readEmailBody source
