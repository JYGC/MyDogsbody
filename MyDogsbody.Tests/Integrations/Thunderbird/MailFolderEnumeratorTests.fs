module MyDogsbody.Tests.Integrations.Thunderbird.MailFolderEnumeratorTests

open System
open System.IO
open Xunit
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

let private alphaStoreDirectory = Path.Combine(measuredShapeProfile, "ImapMail", "imap.alpha.example.com")

[<Fact; Trait("Level", "Integration")>]
let ``enumerate finds an extensionless file as a folder and its .sbd sibling as children, three levels deep`` () =
    let folders = MailFolderEnumerator.enumerate alphaStoreDirectory Mbox

    let byPath path = folders |> List.tryFind (fun folder -> folder.RelativePath = path)

    Assert.True((byPath "Music").IsSome)
    Assert.True((byPath "Music/Surrey Hills Orchestra").IsSome)
    Assert.True((byPath "Music/Surrey Hills Orchestra/Rehearsals").IsSome)

[<Fact; Trait("Level", "Integration")>]
let ``enumerate ignores .msf files entirely, including one with no matching mbox`` () =
    let folders = MailFolderEnumerator.enumerate alphaStoreDirectory Mbox

    Assert.DoesNotContain(folders, fun folder -> folder.DisplayName.EndsWith(".msf", StringComparison.OrdinalIgnoreCase))
    Assert.DoesNotContain(folders, fun folder -> folder.RelativePath = "Archives")
    Assert.DoesNotContain(folders, fun folder -> folder.RelativePath = "OldStuff")

[<Fact; Trait("Level", "Integration")>]
let ``enumerate excludes Trash, Junk, Sent and Drafts from the scannable set`` () =
    let folders = MailFolderEnumerator.enumerate alphaStoreDirectory Mbox

    let scannability name =
        folders |> List.find (fun folder -> folder.RelativePath = name) |> fun folder -> folder.IsScannable

    Assert.False(scannability "Trash")
    Assert.False(scannability "Junk")
    Assert.False(scannability "Sent")
    Assert.False(scannability "Drafts")
    Assert.True(scannability "INBOX")
    Assert.True(scannability "Music")

[<Fact; Trait("Level", "Integration")>]
let ``enumerate reports each folder's on-disk size`` () =
    let folders = MailFolderEnumerator.enumerate alphaStoreDirectory Mbox

    let inbox = folders |> List.find (fun folder -> folder.RelativePath = "INBOX")
    Assert.True(inbox.SizeBytes > 0L)

[<Fact; Trait("Level", "Integration")>]
let ``enumerate treats a zero-byte mbox file as an empty folder, not an error`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"mdb-tbfolders-{Guid.NewGuid()}")
    Directory.CreateDirectory tempDir |> ignore

    try
        File.WriteAllText(Path.Combine(tempDir, "Empty"), "")

        let folders = MailFolderEnumerator.enumerate tempDir Mbox

        let empty = Assert.Single folders
        Assert.Equal("Empty", empty.RelativePath)
        Assert.Equal(0L, empty.SizeBytes)
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``enumerate pairs a folder whose mbox and .sbd differ in case`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"mdb-tbfolders-{Guid.NewGuid()}")
    Directory.CreateDirectory tempDir |> ignore

    try
        File.WriteAllText(Path.Combine(tempDir, "Notes"), "content")
        Directory.CreateDirectory(Path.Combine(tempDir, "NOTES.SBD")) |> ignore
        File.WriteAllText(Path.Combine(tempDir, "NOTES.SBD", "Child"), "child content")

        let folders = MailFolderEnumerator.enumerate tempDir Mbox

        Assert.Contains(folders, fun folder -> folder.RelativePath = "Notes")
        Assert.Contains(folders, fun folder -> folder.RelativePath = "Notes/Child")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``enumerate treats a directory holding cur, new and tmp as a maildir folder`` () =
    let storeDirectory = Path.Combine(maildirShapeProfile, "Mail", "imap.maildir.example.com")

    let folders = MailFolderEnumerator.enumerate storeDirectory Maildir

    let inbox = Assert.Single folders
    Assert.Equal("INBOX", inbox.RelativePath)
    Assert.True(inbox.SizeBytes > 0L)
