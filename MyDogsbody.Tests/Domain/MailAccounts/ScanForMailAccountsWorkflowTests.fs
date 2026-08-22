module MyDogsbody.Tests.Domain.MailAccounts.ScanForMailAccountsWorkflowTests

open Xunit
open MyDogsbody.Domain.MailAccounts

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private accountId id = MailAccountId.create id |> valueOrFail
let private profileRoot path = ProfileRootPath.create path |> valueOrFail

let private account id : DiscoveredMailAccount =
    {
        Id = accountId id
        ProfilePath = @"C:\Thunderbird\Profiles\default"
        DisplayName = $"Account {id}"
        EmailAddresses = [ $"{id}@example.com" ]
        StoreFormat = Mbox
        StoreDirectory = @"C:\Thunderbird\Profiles\default\ImapMail\example.com"
        StoreDirectoryExists = true
        Folders = []
        CachedMessageCount = None
    }

let private discoveryResult accounts : DiscoveryResult =
    {
        Accounts = accounts
        ProfilesFound = [ @"C:\Thunderbird\Profiles\default" ]
        Unreadable = [ { Path = @"C:\Thunderbird\Profiles\orphan"; Reason = "Access denied" } ]
    }

let private loadRootSet: LoadProfileRoot = fun () -> Ok (Some (profileRoot @"C:\Thunderbird"))
let private loadRootUnset: LoadProfileRoot = fun () -> Ok None

/// Records every discover call, so "discoverMailAccounts was never called" is assertable.
let private recordingDiscover (outcome: DiscoveryResult) =
    let received = ResizeArray<ProfileRootPath>()

    let discover: DiscoverMailAccounts =
        fun path ->
            received.Add path
            Ok outcome

    discover, received

let private recordingSaveAccounts () =
    let received = ResizeArray<DiscoveredMailAccount list>()
    let save: SaveMailAccounts = fun accounts -> received.Add accounts; Ok ()
    save, received

let private recordingSaveSelection () =
    let received = ResizeArray<MailAccountId option>()
    let save: SaveSelectedMailAccount = fun selected -> received.Add selected; Ok ()
    save, received

[<Fact; Trait("Level", "Unit")>]
let ``scanForMailAccounts discovers, stores every field, and leaves a still-present selection alone`` () =
    let accounts = [ account "1"; account "2" ]
    let result = discoveryResult accounts
    let discover, discoverCalls = recordingDiscover result
    let saveAccounts, savedAccounts = recordingSaveAccounts ()
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok (Some (accountId "1"))
    let saveSelected, selectionSaves = recordingSaveSelection ()

    let actual =
        ScanForMailAccountsWorkflow.scanForMailAccounts
            loadRootSet
            discover
            saveAccounts
            loadSelected
            saveSelected
            ()

    match actual with
    | Ok discovery ->
        Assert.Equal<DiscoveredMailAccount list>(accounts, discovery.Accounts)
        Assert.Equal<string list>([ @"C:\Thunderbird\Profiles\default" ], discovery.ProfilesFound)
        Assert.Single discovery.Unreadable |> ignore
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Equal<ProfileRootPath list>([ profileRoot @"C:\Thunderbird" ], List.ofSeq discoverCalls)
    Assert.Equal<DiscoveredMailAccount list list>([ accounts ], List.ofSeq savedAccounts)
    Assert.Empty selectionSaves

[<Fact; Trait("Level", "Unit")>]
let ``scanForMailAccounts fails with ProfileRootMissing and never discovers when no root is set`` () =
    let discover, discoverCalls = recordingDiscover (discoveryResult [])
    let saveAccounts, savedAccounts = recordingSaveAccounts ()
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok None
    let saveSelected, _ = recordingSaveSelection ()

    let actual =
        ScanForMailAccountsWorkflow.scanForMailAccounts
            loadRootUnset
            discover
            saveAccounts
            loadSelected
            saveSelected
            ()

    Assert.Equal(Error ProfileRootMissing, actual)
    Assert.Empty discoverCalls
    Assert.Empty savedAccounts

[<Fact; Trait("Level", "Unit")>]
let ``scanForMailAccounts clears a selection naming an account absent from the fresh discovery`` () =
    let accounts = [ account "1" ]
    let discover, _ = recordingDiscover (discoveryResult accounts)
    let saveAccounts, _ = recordingSaveAccounts ()
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok (Some (accountId "gone"))
    let saveSelected, selectionSaves = recordingSaveSelection ()

    let actual =
        ScanForMailAccountsWorkflow.scanForMailAccounts
            loadRootSet
            discover
            saveAccounts
            loadSelected
            saveSelected
            ()

    match actual with
    | Ok _ -> ()
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let cleared = Assert.Single selectionSaves
    Assert.Equal(None, cleared)

[<Fact; Trait("Level", "Unit")>]
let ``scanForMailAccounts carries unreadable directories into the result rather than failing`` () =
    let discover, _ = recordingDiscover (discoveryResult [])
    let saveAccounts, _ = recordingSaveAccounts ()
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok None
    let saveSelected, _ = recordingSaveSelection ()

    let actual =
        ScanForMailAccountsWorkflow.scanForMailAccounts
            loadRootSet
            discover
            saveAccounts
            loadSelected
            saveSelected
            ()

    match actual with
    | Ok discovery -> Assert.Single discovery.Unreadable |> ignore
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
