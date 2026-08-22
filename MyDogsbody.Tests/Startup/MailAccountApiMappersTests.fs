module MyDogsbody.Tests.Startup.MailAccountApiMappersTests

open System
open Xunit
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private anAction = ActionNames.MyDogsbody.Startup.MailAccountApi.getAccounts

// ---------- domain <-> UI record ----------

[<Fact; Trait("Level", "Unit")>]
let ``toMailFolderUiType carries every field`` () =
    let folder: MailFolder =
        { RelativePath = "Music/Surrey Hills Orchestra"; DisplayName = "Surrey Hills Orchestra"; SizeBytes = 12345L; IsScannable = true }

    let actual = MailAccountApiMappers.toMailFolderUiType folder

    Assert.Equal("Music/Surrey Hills Orchestra", actual.RelativePath)
    Assert.Equal("Surrey Hills Orchestra", actual.DisplayName)
    Assert.Equal(12345L, actual.SizeBytes)
    Assert.True actual.IsScannable

[<Fact; Trait("Level", "Unit")>]
let ``toMailAccountUiType carries every field including a cached count`` () =
    let account: DiscoveredMailAccount =
        {
            Id = MailAccountId.create @"C:\profile|account1" |> valueOrFail
            ProfilePath = @"C:\profile"
            DisplayName = "Alpha Mail"
            EmailAddresses = [ "alice@alpha.example.com" ]
            StoreFormat = Mbox
            StoreDirectory = @"C:\profile\ImapMail\imap.alpha.example.com"
            StoreDirectoryExists = true
            Folders = [ { RelativePath = "INBOX"; DisplayName = "INBOX"; SizeBytes = 10L; IsScannable = true } ]
            CachedMessageCount = Some(4, DateTime(2026, 8, 20))
        }

    let actual = MailAccountApiMappers.toMailAccountUiType account

    Assert.Equal(@"C:\profile|account1", actual.Id)
    Assert.Equal("Alpha Mail", actual.DisplayName)
    Assert.Equal<string list>([ "alice@alpha.example.com" ], actual.EmailAddresses)
    Assert.Equal("Mbox", actual.StoreFormat)
    Assert.Equal(@"C:\profile\ImapMail\imap.alpha.example.com", actual.StoreDirectory)
    Assert.True actual.StoreDirectoryExists
    let folder = Assert.Single actual.Folders
    Assert.Equal("INBOX", folder.RelativePath)
    Assert.Equal(Some(4, DateTime(2026, 8, 20)), actual.CachedMessageCount)

[<Fact; Trait("Level", "Unit")>]
let ``toMailAccountUiType maps Maildir to its UI string`` () =
    let account: DiscoveredMailAccount =
        {
            Id = MailAccountId.create "a" |> valueOrFail
            ProfilePath = "p"
            DisplayName = "d"
            EmailAddresses = []
            StoreFormat = Maildir
            StoreDirectory = "s"
            StoreDirectoryExists = false
            Folders = []
            CachedMessageCount = None
        }

    let actual = MailAccountApiMappers.toMailAccountUiType account

    Assert.Equal("Maildir", actual.StoreFormat)
    Assert.Equal(None, actual.CachedMessageCount)

[<Fact; Trait("Level", "Unit")>]
let ``toDiscoveryResultUiType carries every field`` () =
    let account: DiscoveredMailAccount =
        {
            Id = MailAccountId.create "a" |> valueOrFail
            ProfilePath = "p"
            DisplayName = "d"
            EmailAddresses = []
            StoreFormat = Mbox
            StoreDirectory = "s"
            StoreDirectoryExists = true
            Folders = []
            CachedMessageCount = None
        }

    let result: DiscoveryResult =
        {
            Accounts = [ account ]
            ProfilesFound = [ @"C:\profile" ]
            Unreadable = [ { Path = @"C:\denied"; Reason = "Access denied" } ]
        }

    let actual = MailAccountApiMappers.toDiscoveryResultUiType result

    Assert.Single actual.Accounts |> ignore
    Assert.Equal<string list>([ @"C:\profile" ], actual.ProfilesFound)
    let unreadable = Assert.Single actual.Unreadable
    Assert.Equal(@"C:\denied", unreadable.Path)
    Assert.Equal("Access denied", unreadable.Reason)

// ---------- error translation ----------

[<Fact; Trait("Level", "Contract")>]
let ``ProfileRootInvalid becomes an unlogged exception carrying the reason`` () =
    let actual = MailAccountApiMappers.toMyDogsbodyException anAction (ProfileRootInvalid "Profile root path must not be empty.")

    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("Profile root path must not be empty.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``ProfileRootMissing becomes an unlogged exception`` () =
    let actual = MailAccountApiMappers.toMyDogsbodyException anAction ProfileRootMissing

    Assert.False(String.IsNullOrWhiteSpace actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``NoProfileFound becomes an unlogged exception naming the searched path`` () =
    let actual = MailAccountApiMappers.toMyDogsbodyException anAction (NoProfileFound @"C:\nowhere")

    Assert.Contains(@"C:\nowhere", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``ProfileRootUnreachable becomes an unlogged exception naming the folder and the reason, distinct from NoProfileFound`` () =
    let actual =
        MailAccountApiMappers.toMyDogsbodyException anAction (ProfileRootUnreachable(@"Z:\gone", "drive not available"))

    Assert.Contains(@"Z:\gone", actual.Message)
    Assert.Contains("drive not available", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

    let noProfileFound = MailAccountApiMappers.toMyDogsbodyException anAction (NoProfileFound @"Z:\gone")
    Assert.NotEqual<string>(noProfileFound.Message, actual.Message)

[<Fact; Trait("Level", "Contract")>]
let ``ProfileUnreadable becomes an unlogged exception - a locked or malformed profile is expected, not a defect`` () =
    let actual = MailAccountApiMappers.toMyDogsbodyException anAction (ProfileUnreadable(@"C:\profile", "Access denied"))

    Assert.Contains(@"C:\profile", actual.Message)
    Assert.Contains("Access denied", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``MailAccountIdInvalid becomes an unlogged exception carrying the reason`` () =
    let actual = MailAccountApiMappers.toMyDogsbodyException anAction (MailAccountIdInvalid "Mail account id must not be empty.")

    Assert.Equal("Mail account id must not be empty.", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``MailAccountNotFound becomes an unlogged exception naming the identifier`` () =
    let id = MailAccountId.create "a" |> valueOrFail

    let actual = MailAccountApiMappers.toMyDogsbodyException anAction (MailAccountNotFound id)

    Assert.Contains("a", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``NoAccountSelected becomes an unlogged exception`` () =
    let actual = MailAccountApiMappers.toMyDogsbodyException anAction NoAccountSelected

    Assert.False(String.IsNullOrWhiteSpace actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``StoreDirectoryMissing becomes an unlogged exception naming the account and the path`` () =
    let id = MailAccountId.create "a" |> valueOrFail

    let actual = MailAccountApiMappers.toMyDogsbodyException anAction (StoreDirectoryMissing(id, @"C:\gone"))

    Assert.Contains("a", actual.Message)
    Assert.Contains(@"C:\gone", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``MailFolderUnreadable becomes an unlogged exception - a locked folder is expected, not a defect`` () =
    let actual = MailAccountApiMappers.toMyDogsbodyException anAction (MailFolderUnreadable("INBOX", "file is locked"))

    Assert.Contains("INBOX", actual.Message)
    Assert.Contains("file is locked", actual.Message)
    Assert.IsType<ApplicationException>(actual.InnerException) |> ignore

[<Fact; Trait("Level", "Contract")>]
let ``MailStoreFailed carries the store's message and is left unmarked, having already been logged by the adapter`` () =
    let actual = MailAccountApiMappers.toMyDogsbodyException anAction (MailStoreFailed "database is locked")

    Assert.Equal(anAction, actual.ActionName)
    Assert.Equal("database is locked", actual.Message)
    Assert.Null actual.InnerException

[<Fact; Trait("Level", "Contract")>]
let ``every MailAccountError case produces a non-empty message and the declared action`` () =
    let id = MailAccountId.create "a" |> valueOrFail

    let allCases: MailAccountError list =
        [
            ProfileRootInvalid "a"
            ProfileRootMissing
            NoProfileFound "b"
            ProfileRootUnreachable("b2", "b3")
            ProfileUnreadable("c", "d")
            MailAccountIdInvalid "e"
            MailAccountNotFound id
            NoAccountSelected
            StoreDirectoryMissing(id, "f")
            MailFolderUnreadable("g", "h")
            MailStoreFailed "i"
        ]

    let declaredCases = Reflection.FSharpType.GetUnionCases(typeof<MailAccountError>) |> Array.length
    Assert.Equal(declaredCases, List.length allCases)

    for case in allCases do
        let actual = MailAccountApiMappers.toMyDogsbodyException anAction case
        Assert.False(String.IsNullOrWhiteSpace actual.Message, $"{case} produced an empty message")
        Assert.Equal(anAction, actual.ActionName)

[<Fact; Trait("Level", "Contract")>]
let ``an adapter exception becomes MailStoreFailed carrying its message`` () =
    let adapterFailure =
        MyDogsbodyException(
            ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.loadMailAccounts,
            "Failed to load mail accounts.",
            InvalidOperationException "disk gone"
        )

    let actual = MailAccountApiMappers.toMailAccountError adapterFailure

    Assert.Equal(MailStoreFailed "Failed to load mail accounts.", actual)

[<Fact; Trait("Level", "Contract")>]
let ``the inbound and outbound translations round trip an adapter message`` () =
    let adapterFailure =
        MyDogsbodyException(
            ActionNames.MyDogsbody.Integrations.Thunderbird.ThunderbirdStore.saveMailAccounts,
            "Failed to save mail accounts.",
            InvalidOperationException "disk gone"
        )

    let actual = adapterFailure |> MailAccountApiMappers.toMailAccountError |> MailAccountApiMappers.toMyDogsbodyException anAction

    Assert.Equal("Failed to save mail accounts.", actual.Message)
