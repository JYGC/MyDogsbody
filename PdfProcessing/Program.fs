open System
open UglyToad.PdfPig
open UglyToad.PdfPig.Content
open MyDogsbody.Builders
open MyDogsbody.Infrastructure

// Explanation: 
// page.GetWords() returns each word with its bounding box.//We group words by their vertical position (BoundingBox.Bottom) rounded with a small tolerance (epsilon) to detect which words are on the same line.
// Then we sort:
// Lines from top to bottom (sortByDescending).
// Words within each line from left to right.
// Finally, print each reconstructed line separately.
// You can adjust epsilon (for example, 1.5 or 3.0) if lines are merging or splitting incorrectly depending on the PDF’s font metrics.


let epsilon = 2.0 // tolerance for Y-coordinate differences (line separation)

let extractLines (page: Page) =
  page.GetWords()
  |> Seq.groupBy (fun w -> Math.Round(w.BoundingBox.Bottom / epsilon))
  |> Seq.sortByDescending fst
  |> Seq.map (fun (_, words) ->
      words
      |> Seq.sortBy (fun w -> w.BoundingBox.Left)
      |> Seq.map (fun w -> w.Text)
      |> String.concat " "
    )

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        printfn "Usage: dotnet run <path-to-pdf>"
        1
    else
        argv[0]
        |> Documents.getPdfObject
        |> Documents.getContentSplitByLines (HandleErrorBuilder (fun ex -> ex.ToString() |> System.Console.WriteLine))
        |> (function
            | Ok lines ->
                for line in lines do
                    printfn "%s" line
            | Error ex ->
                eprintfn "Error: %s" ex.Message
        )
        |> ignore
        0
    //try
    //  use document = PdfDocument.Open(pdfPath)
    //  for page in document.GetPages() do
    //    printfn "--- Page %d ---" page.Number
    //    for line in extractLines page do
    //      printfn "%s" line
    //  0
    //with
    //| :? System.IO.FileNotFoundException ->
    //    eprintfn "File not found: %s" pdfPath
    //    1
    //| ex ->
    //    eprintfn "Error: %s" ex.Message
    //    1
