/// The documents adapter: the function that satisfies the domain's ReadDocumentContent.
///
/// PdfPig lives here and nowhere else. The workflow that groups words into lines sees a function
/// value, so it needs no file and no library to be tested.
module MyDogsbody.Integrations.Documents.PdfDocumentReader

open System
open System.IO
open UglyToad.PdfPig
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Documents

let private baselineDistanceInPdfUnitsWithinWhichWordsAreOneLine = 2.0

/// A block is what Finding 4's join rule is scoped to: a wrapped continuation may be joined to its
/// predecessor within a block and never across one.
let private lineGapAsAMultipleOfThePageLinePitchAboveWhichANewBlockStarts = 1.8

/// Returns the band key (larger = higher up the page) alongside the text so the caller can
/// measure vertical gaps.
let private groupOnePagesWordsIntoLinesByBaselineBandTopOfThePageFirstEachBandReadLeftToRight
    (words: (float * float * string) list)
    : (float * string) list =
    words
    |> List.groupBy (fun (bottom, _, _) -> Math.Round(bottom / baselineDistanceInPdfUnitsWithinWhichWordsAreOneLine))
    |> List.sortByDescending fst
    |> List.map (fun (band, banded) ->
        let text =
            banded
            |> List.sortBy (fun (_, left, _) -> left)
            |> List.map (fun (_, _, wordText) -> wordText)
            |> String.concat " "

        band, text)

let private tagEveryLineOfOnePageWithItsBlockIndexReturningTheNextFreeBlockIndex
    (startBlock: int)
    (lines: (float * string) list)
    : TextLine list * int =
    let gaps =
        lines
        |> List.pairwise
        |> List.map (fun ((upper, _), (lower, _)) -> upper - lower)
        |> List.filter (fun gap -> gap > 0.0)

    let pitch = if List.isEmpty gaps then infinity else List.min gaps

    let tagged, lastBlock =
        lines
        |> List.fold
            (fun (reversedTaggedLines, block, previousBand) (band, text) ->
                let block =
                    match previousBand with
                    | Some prev when prev - band > lineGapAsAMultipleOfThePageLinePitchAboveWhichANewBlockStarts * pitch ->
                        block + 1
                    | _ -> block

                ({ Text = text; BlockIndex = block } :: reversedTaggedLines, block, Some band))
            ([], startBlock, None)
        |> fun (reversedTaggedLines, block, _) -> List.rev reversedTaggedLines, block

    tagged, lastBlock + 1

/// Satisfies the domain's ReadDocumentText.
///
/// Returns DocumentError directly rather than going through handleError: every outcome here is a
/// domain fact the scan reports as a problem - a scanned image (DocumentHasNoTextLayer), a file
/// that will not open (DocumentUnreadable). No OCR: 94.7% of measured PDFs have a text layer.
let readText (source: DocumentSource) : Result<TextLine list, DocumentError> =
    if isNull source.Content || source.Content.Length = 0 then
        Error(DocumentUnreadable "The document is empty.")
    else
        try
            use stream = new MemoryStream(source.Content)
            use document = PdfDocument.Open stream

            let wordsByPage =
                [ for page in document.GetPages() ->
                      [ for word in page.GetWords() ->
                            word.BoundingBox.Bottom, word.BoundingBox.Left, word.Text ] ]

            if wordsByPage |> List.forall List.isEmpty then
                Error DocumentHasNoTextLayer
            else
                let lines, _ =
                    wordsByPage
                    |> List.fold
                        (fun (accumulatedLines, nextBlock) words ->
                            let tagged, nextBlock =
                                tagEveryLineOfOnePageWithItsBlockIndexReturningTheNextFreeBlockIndex
                                    nextBlock
                                    (groupOnePagesWordsIntoLinesByBaselineBandTopOfThePageFirstEachBandReadLeftToRight words)
                            accumulatedLines @ tagged, nextBlock)
                        ([], 0)

                Ok lines
        with caughtException ->
            Error(DocumentUnreadable caughtException.Message)

let readContent
    (handleError: HandleErrorBuilder)
    (path: DocumentPath)
    : Result<DocumentContent, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Documents.PdfDocumentReader.readContent
    let pdfPath = DocumentPath.value path

    handleError {
        try
            // A missing file is expected rather than exceptional, so it is wrapped around an
            // ApplicationException: HandleErrorBuilder lets that through unlogged.
            let! document =
                match File.Exists pdfPath with
                | true -> PdfDocument.Open pdfPath |> Ok
                | false ->
                    let message = $"PDF file does not exist: {pdfPath}"
                    MyDogsbodyException(action, message, ApplicationException message) |> Error

            use document = document

            return {
                Words =
                    [
                        for page in document.GetPages() do
                            for word in page.GetWords() do
                                yield
                                    {
                                        Text = word.Text
                                        BottomEdgeHeightAboveThePageBottom = word.BoundingBox.Bottom
                                        LeftEdgeDistanceFromThePageLeft = word.BoundingBox.Left
                                    }
                    ]
            }
        with caughtException ->
            return! MyDogsbodyException(action, "Failed to extract content from PDF.", caughtException)
    }
