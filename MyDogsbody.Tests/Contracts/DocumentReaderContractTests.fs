module MyDogsbody.Tests.Contracts.DocumentReaderContractTests

open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Documents
open MyDogsbody.Tests.Fixtures

// friction #7: ReadDocumentText and ReadDocumentContent both mean "read a document", so each
// gets its OWN shared contract suite rather than one covering both - a single suite would hide a
// composition root that bound the wrong one.

let private handleError = HandleErrorBuilder ignore

// ============================ ReadDocumentText ============================

let private realReadDocumentText: ReadDocumentText =
    DocumentReaders.dispatch
        PdfDocumentReader.readText
        WordDocumentReader.readText
        PlainTextDocumentReader.readText
        EmailBodyReader.readText

let private fakeReadDocumentText: ReadDocumentText =
    fun source ->
        if source.Content.Length = 0 then
            Error(DocumentUnreadable "empty")
        else
            Ok [ { Text = $"line from {source.Name}"; BlockIndex = 0 } ]

/// Public: xUnit resolves MemberData by reflection on the compiled class.
let textImplementations: obj[] seq = [ [| box "real" |]; [| box "fake" |] ]

let private readDocumentText name : ReadDocumentText =
    match name with
    | "real" -> realReadDocumentText
    | "fake" -> fakeReadDocumentText
    | other -> failwith $"unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof textImplementations)>]
let ``ReadDocumentText: a readable document yields non-empty block-tagged lines`` (implementation: string) =
    let read = readDocumentText implementation
    let source = { Format = Pdf; Name = "invoice.pdf"; Content = DocumentFixtures.pdfWithText [ "Invoice Number 1"; "Total 10.00" ] }

    match read source with
    | Ok lines ->
        Assert.NotEmpty lines
        Assert.All(lines, fun line -> Assert.True(line.BlockIndex >= 0))
    | Error err -> Assert.Fail($"expected Ok, got {err}")

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof textImplementations)>]
let ``ReadDocumentText: zero bytes is DocumentUnreadable, never an empty success`` (implementation: string) =
    match readDocumentText implementation { Format = PlainText; Name = "x.txt"; Content = [||] } with
    | Error(DocumentUnreadable _) -> ()
    | other -> Assert.Fail($"expected DocumentUnreadable, got {other}")

// ============================ ReadDocumentContent ============================

let private realReadDocumentContent: ReadDocumentContent =
    fun path ->
        PdfDocumentReader.readContent handleError path
        |> Result.mapError (fun ex -> DocumentUnreadable ex.Message)

let private fakeReadDocumentContent: ReadDocumentContent =
    fun path ->
        if File.Exists(DocumentPath.value path) then
            Ok { Words = [ { Text = "Alpha"; Bottom = 700.0; Left = 50.0 } ] }
        else
            Error(DocumentUnreadable "not found")

let contentImplementations: obj[] seq = [ [| box "real" |]; [| box "fake" |] ]

let private readDocumentContent name : ReadDocumentContent =
    match name with
    | "real" -> realReadDocumentContent
    | "fake" -> fakeReadDocumentContent
    | other -> failwith $"unknown implementation '{other}'"

let private pathOf value =
    match DocumentPath.create value with
    | Ok p -> p
    | Error e -> failwith e

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof contentImplementations)>]
let ``ReadDocumentContent: a missing file is DocumentUnreadable`` (implementation: string) =
    let path = pathOf (Path.Combine(Path.GetTempPath(), $"{System.Guid.NewGuid()}.pdf"))

    match readDocumentContent implementation path with
    | Error(DocumentUnreadable _) -> ()
    | other -> Assert.Fail($"expected DocumentUnreadable, got {other}")

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof contentImplementations)>]
let ``ReadDocumentContent: a readable PDF yields words with real coordinates`` (implementation: string) =
    let file = Path.Combine(Path.GetTempPath(), $"{System.Guid.NewGuid()}.pdf")
    File.WriteAllBytes(file, DocumentFixtures.pdfWithText [ "Alpha"; "Beta" ])

    try
        match readDocumentContent implementation (pathOf file) with
        | Ok content ->
            Assert.NotEmpty content.Words
            Assert.All(content.Words, fun w -> Assert.True(w.Left >= 0.0 && w.Bottom >= 0.0))
        | Error err -> Assert.Fail($"expected Ok, got {err}")
    finally
        try File.Delete file with _ -> ()
