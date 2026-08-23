module MyDogsbody.Tests.Domain.MailAccounts.ListMailAccountsWorkflowTests

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

[<Fact; Trait("Level", "Unit")>]
let ``listMailAccounts returns the stored accounts together with the selection`` () =
    let accounts = [ account "1"; account "2" ]
    let load: LoadMailAccounts = fun () -> Ok accounts
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok (Some (MailAccountId.create "1" |> valueOrFail))

    let actual = ListMailAccountsWorkflow.listMailAccounts load loadSelected ()

    match actual with
    | Ok (returnedAccounts, selected) ->
        Assert.Equal<DiscoveredMailAccount list>(accounts, returnedAccounts)
        Assert.Equal(Some (MailAccountId.create "1" |> valueOrFail), selected)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``listMailAccounts returns an empty list and no selection when the store is empty`` () =
    let load: LoadMailAccounts = fun () -> Ok []
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok None

    let actual = ListMailAccountsWorkflow.listMailAccounts load loadSelected ()

    Assert.Equal(Ok ([], None), actual)

[<Fact; Trait("Level", "Unit")>]
let ``listMailAccounts returns the store's failure unchanged`` () =
    let load: LoadMailAccounts = fun () -> Error (MailStoreFailed "unreachable")
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok None

    let actual = ListMailAccountsWorkflow.listMailAccounts load loadSelected ()

    Assert.Equal(Error (MailStoreFailed "unreachable"), actual)
