module MyDogsbody.Tests.Domain.MailAccounts.SelectMailAccountWorkflowTests

open Xunit
open MyDogsbody.Domain.MailAccounts

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private account id : DiscoveredMailAccount =
    {
        Id = MailAccountId.create id |> valueOrFail
        ProfilePath = @"C:\Thunderbird\Profiles\default"
        DisplayName = $"Account {id}"
        EmailAddresses = [ $"{id}@example.com" ]
        StoreFormat = Mbox
        StoreDirectory = @"C:\Thunderbird\Profiles\default\ImapMail\example.com"
        StoreDirectoryExists = true
        Folders = []
        CachedMessageCount = None
    }

/// Records every selection save, so "the store was never reached" is assertable.
let private recordingSaveSelection () =
    let received = ResizeArray<MailAccountId option>()
    let save: SaveSelectedMailAccount = fun selected -> received.Add selected; Ok ()
    save, received

[<Fact; Trait("Level", "Unit")>]
let ``selectMailAccount selects a known account and persists it`` () =
    let load: LoadMailAccounts = fun () -> Ok [ account "1"; account "2" ]
    let save, received = recordingSaveSelection ()

    let actual = SelectMailAccountWorkflow.selectMailAccount load save "1"

    match actual with
    | Ok id -> Assert.Equal("1", MailAccountId.value id)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let saved = Assert.Single received
    Assert.Equal(Some (MailAccountId.create "1" |> valueOrFail), saved)

[<Fact; Trait("Level", "Unit")>]
let ``selectMailAccount rejects an id not among the stored accounts and never reaches the store`` () =
    let load: LoadMailAccounts = fun () -> Ok [ account "1" ]
    let save, received = recordingSaveSelection ()

    let actual = SelectMailAccountWorkflow.selectMailAccount load save "unknown"

    Assert.Equal(Error (MailAccountNotFound (MailAccountId.create "unknown" |> valueOrFail)), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``selectMailAccount rejects an empty id and never reaches the store`` () =
    let load: LoadMailAccounts = fun () -> Ok [ account "1" ]
    let save, received = recordingSaveSelection ()

    let actual = SelectMailAccountWorkflow.selectMailAccount load save "   "

    Assert.Equal(Error (MailAccountIdInvalid "Mail account id must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``selectMailAccount returns the store's failure unchanged`` () =
    let load: LoadMailAccounts = fun () -> Ok [ account "1" ]
    let save: SaveSelectedMailAccount = fun _ -> Error (MailStoreFailed "disk full")

    let actual = SelectMailAccountWorkflow.selectMailAccount load save "1"

    Assert.Equal(Error (MailStoreFailed "disk full"), actual)
