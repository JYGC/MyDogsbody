module MyDogsbody.Domain.Documents.ReadDocumentLinesWorkflow

open System
open MyDogsbody.Domain
open MyDogsbody.Domain.Documents

let private verticalDistanceWithinWhichWordsAreTreatedAsOneLine = 2.0

/// A pure decision, so it lives here rather than in the adapter - it needs no file, no builder
/// and no ActionName, and it is the whole reason this area has a workflow at all.
let private groupWordsIntoLinesByVerticalBandTopOfThePageFirstEachBandReadLeftToRight
    (content: DocumentContent)
    : string list =
    content.Words
    |> Seq.groupBy (fun word ->
        Math.Round(word.BottomEdgeHeightAboveThePageBottom / verticalDistanceWithinWhichWordsAreTreatedAsOneLine))
    |> Seq.sortByDescending fst
    |> Seq.map (fun (_, words) ->
        words
        |> Seq.sortBy (fun word -> word.LeftEdgeDistanceFromThePageLeft)
        |> Seq.map (fun word -> word.Text)
        |> String.concat " "
    )
    |> Seq.toList

let readDocumentLines
    (readDocumentContent: ReadDocumentContent)
    (input: string)
    : Result<string list, DocumentError> =
    result {
        let! path =
            DocumentPath.create input
            |> Result.mapError DocumentPathInvalid

        let! content = readDocumentContent path
        return groupWordsIntoLinesByVerticalBandTopOfThePageFirstEachBandReadLeftToRight content
    }
