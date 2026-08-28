module MyDogsbody.Tests.Integrations.Documents.EmailBodyReaderTests

open System.IO
open System.Text
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Documents
open MyDogsbody.Tests.Fixtures

let private htmlSource (html: string) : DocumentSource =
    { Format = EmailBody; Name = "body.html"; Content = Encoding.UTF8.GetBytes html }

[<Fact; Trait("Level", "Integration")>]
let ``readText keeps a table label and its value adjacent in one block`` () =
    // Finding 5: the HTML alternative preserves the label-to-value adjacency the plain-text
    // alternative destroyed by wrapping.
    let html = File.ReadAllText DocumentFixtures.tableBodyHtml

    // Act
    let actual = EmailBodyReader.readText { htmlSource html with Content = File.ReadAllBytes DocumentFixtures.tableBodyHtml }

    // Assert
    match actual with
    | Ok lines ->
        let texts = lines |> List.map (fun line -> line.Text)
        Assert.Contains("Invoice Number", texts)
        Assert.Contains("HTML-500", texts)

        // the label and its value are consecutive and in the same block
        let labelLine = lines |> List.find (fun line -> line.Text = "Invoice Number")
        let valueLine = lines |> List.find (fun line -> line.Text = "HTML-500")
        Assert.Equal(labelLine.BlockIndex, valueLine.BlockIndex)
        Assert.Equal(1, (lines |> List.findIndex (fun l -> l.Text = "HTML-500")) - (lines |> List.findIndex (fun l -> l.Text = "Invoice Number")))
    | Error err -> Assert.Fail($"Expected Ok, got Error {err}")

[<Fact; Trait("Level", "Integration")>]
let ``readText puts separate rows in separate blocks and a paragraph in its own block`` () =
    let actual = EmailBodyReader.readText (htmlSource (File.ReadAllText DocumentFixtures.tableBodyHtml))

    match actual with
    | Ok lines ->
        let block t = (lines |> List.find (fun l -> l.Text = t)).BlockIndex
        Assert.NotEqual<int>(block "Invoice Number", block "Amount Due")
        Assert.NotEqual<int>(block "Please find your invoice below.", block "Invoice Number")
    | Error err -> Assert.Fail($"Expected Ok, got Error {err}")

[<Fact; Trait("Level", "Unit")>]
let ``readText strips markup when the HTML has no table structure`` () =
    let actual =
        EmailBodyReader.readText (htmlSource "<html><body><p>Attached is your invoice for this month.</p></body></html>")

    match actual with
    | Ok [ line ] -> Assert.Equal("Attached is your invoice for this month.", line.Text)
    | other -> Assert.Fail($"Expected a single stripped line, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``readText decodes HTML entities`` () =
    let actual =
        EmailBodyReader.readText (htmlSource "<body><p>Smith&nbsp;&amp;&nbsp;Sons &mdash; total&nbsp;$5</p></body>")

    match actual with
    | Ok [ line ] -> Assert.Equal("Smith & Sons — total $5", line.Text)
    | other -> Assert.Fail($"Expected one decoded line, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``readText reports DocumentUnreadable for zero bytes`` () =
    match EmailBodyReader.readText { Format = EmailBody; Name = "body.html"; Content = [||] } with
    | Error (DocumentUnreadable _) -> ()
    | other -> Assert.Fail($"Expected Error (DocumentUnreadable _), got {other}")
