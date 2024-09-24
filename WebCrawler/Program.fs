open System
open FSharp.Data

[<EntryPoint>]
let main args =
  let url = args[0]
  let page = HtmlDocument.Load url

  let articleIds =
    page.CssSelect("article")
    |> Seq.map (fun element ->
      element.Attribute("id").ToString())
    |> Seq.toList
    |> String.concat ";"
  
  Console.WriteLine articleIds
  
  // let html =
  //   page.Body()
  
  // let dd = html.ToString()
  
  // Console.WriteLine dd

  0