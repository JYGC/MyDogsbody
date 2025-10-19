open System
open UglyToad.PdfPig
open UglyToad.PdfPig.Content

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
    let pdfPath = argv[0]
    try
      use document = PdfDocument.Open(pdfPath)
      for page in document.GetPages() do
        printfn "--- Page %d ---" page.Number
        for line in extractLines page do
          printfn "%s" line
      0
    with
    | :? System.IO.FileNotFoundException ->
        eprintfn "File not found: %s" pdfPath
        1
    | ex ->
        eprintfn "Error: %s" ex.Message
        1
