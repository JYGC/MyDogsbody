module MyDogsbody.Domain.Documents.ReadDocumentLinesWorkflow

open System
open MyDogsbody.Domain
open MyDogsbody.Domain.Documents

/// Words within this many units of each other vertically are treated as one line.
let private lineTolerance = 2.0

/// Groups words into lines: bands by vertical position, top of the page first, each band read
/// left to right.
///
/// A pure decision, so it lives here rather than in the adapter - it needs no file, no builder
/// and no ActionName, and it is the whole reason this area has a workflow at all.
let private toLines (content: DocumentContent) : string list =
    content.Words
    |> Seq.groupBy (fun word -> Math.Round(word.Bottom / lineTolerance))
    |> Seq.sortByDescending fst
    |> Seq.map (fun (_, words) ->
        words
        |> Seq.sortBy (fun word -> word.Left)
        |> Seq.map (fun word -> word.Text)
        |> String.concat " "
    )
    |> Seq.toList

/// Reads a document and returns its text, one string per line.
let readDocumentLines
    (readDocumentContent: ReadDocumentContent)
    (input: string)
    : Result<string list, DocumentError> =
    result {
        let! path =
            DocumentPath.create input
            |> Result.mapError DocumentPathInvalid

        let! content = readDocumentContent path
        return toLines content
    }
