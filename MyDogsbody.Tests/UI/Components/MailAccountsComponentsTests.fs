module MyDogsbody.Tests.UI.Components.MailAccountsComponentsTests

open Xunit
open MyDogsbody.UI.Types
open MyDogsbody.UI.Portal.Components

// The accounts table has to state each account's on-disk size, and what a scan of it will
// actually cost - requirements.md -> "Selecting an account" and "User interface" list on-disk
// size per row, and "Listing folders" gives the reason: the page states the cost before the scan
// is run. The two figures differ because Trash/Deleted/Junk/Sent/Drafts are excluded from the
// scannable set (Q4.8 - 9.0 GB of 15.2 GB on the measured profile).

let private folder relativePath sizeBytes isScannable : MailFolderUiType =
    {
        RelativePath = relativePath
        DisplayName = relativePath
        SizeBytes = sizeBytes
        IsScannable = isScannable
    }

let private account (folders: MailFolderUiType list) : MailAccountUiType =
    {
        Id = "profile|account1"
        ProfilePath = @"C:\profile"
        DisplayName = "Alpha Mail"
        EmailAddresses = [ "alpha@example.com" ]
        StoreFormat = "Mbox"
        StoreDirectory = @"C:\profile\ImapMail\imap.alpha.example.com"
        StoreDirectoryExists = true
        Folders = folders
        CachedMessageCount = None
    }

[<Theory; Trait("Level", "Unit")>]
[<InlineData(0L, "0 bytes")>]
[<InlineData(1L, "1 bytes")>]
[<InlineData(1023L, "1023 bytes")>]
[<InlineData(1024L, "1.0 KB")>]
[<InlineData(1536L, "1.5 KB")>]
[<InlineData(1048575L, "1024.0 KB")>]
[<InlineData(1048576L, "1.0 MB")>]
[<InlineData(1073741824L, "1.0 GB")>]
[<InlineData(2684354560L, "2.5 GB")>]
let ``formatSizeBytes states a size in the units a person reads`` (bytes: int64, expected: string) =
    Assert.Equal(expected, MailAccountsComponents.formatSizeBytes bytes)

[<Fact; Trait("Level", "Unit")>]
let ``accountSizeBytes sums every folder, scannable or not`` () =
    let actual =
        account [ folder "INBOX" 1000L true; folder "Trash" 9000L false; folder "Music" 24L true ]
        |> MailAccountsComponents.accountSizeBytes

    Assert.Equal(10024L, actual)

[<Fact; Trait("Level", "Unit")>]
let ``scannableSizeBytes counts only the folders a scan will actually read`` () =
    let actual =
        account [ folder "INBOX" 1000L true; folder "Trash" 9000L false; folder "Music" 24L true ]
        |> MailAccountsComponents.scannableSizeBytes

    Assert.Equal(1024L, actual)

[<Fact; Trait("Level", "Unit")>]
let ``an account with no folders is zero on both figures rather than failing`` () =
    let empty = account []

    Assert.Equal(0L, MailAccountsComponents.accountSizeBytes empty)
    Assert.Equal(0L, MailAccountsComponents.scannableSizeBytes empty)
