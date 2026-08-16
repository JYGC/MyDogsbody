module MyDogsbody.Tests.UI.Components.TemplatesComponentsTests

open System
open System.IO
open System.Threading
open Xunit
open Microsoft.AspNetCore.Components.Forms
open MyDogsbody.UI.Portal.Components

/// The smallest thing that is an IBrowserFile - only Name is ever read (AttachmentName matches a
/// filename, not the file's bytes), but the interface has to be satisfied whole.
type private StubBrowserFile(name: string) =
    interface IBrowserFile with
        member _.Name = name
        member _.LastModified = DateTimeOffset.MinValue
        member _.Size = 0L
        member _.ContentType = "application/pdf"
        member _.OpenReadStream(_maxAllowedSize: int64, _cancellationToken: CancellationToken) : Stream = Stream.Null

// PR #14 review: MudBlazor invokes FilesChanged with default(T) - null for IBrowserFile -
// whenever the selection is cleared or reset (ClearAsync / ResetValueAsync, and the picker's
// cancel path). Reading file.Name straight off that raised a NullReferenceException from inside
// a Blazor event handler, which unwinds to Shell.fs's ErrorBoundary' and blanks the dialog
// rather than surfacing an alert.

[<Fact; Trait("Level", "Unit")>]
let ``the attachment filename of a chosen file is its name`` () =
    let actual = TemplatesComponents.attachmentFilenameOf (StubBrowserFile "statement.pdf")

    Assert.Equal("statement.pdf", actual)

[<Fact; Trait("Level", "Unit")>]
let ``the attachment filename of a cleared selection is empty rather than a raised exception`` () =
    let actual = TemplatesComponents.attachmentFilenameOf null

    Assert.Equal("", actual)
