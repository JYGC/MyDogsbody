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
        // What a DiscoverMailAccounts adapter always reports: it never sees the stored selection.
        // The workflow is what decides the value that comes back out.
        SelectionCleared = false
    }

let private loadRootSet: LoadProfileRoot = fun () -> Ok (Some (profileRoot @"C:\Thunderbird"))
let private loadRootUnset: LoadProfileRoot = fun () -> Ok None

/// What a first-ever scan sees: nothing stored yet.
let private loadNoStoredAccounts: LoadMailAccounts = fun () -> Ok []

let private loadStoredAccounts accounts : LoadMailAccounts = fun () -> Ok accounts

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
            loadNoStoredAccounts
            saveAccounts
            loadSelected
            saveSelected
            ()

    match actual with
    | Ok discovery ->
        Assert.Equal<DiscoveredMailAccount list>(accounts, discovery.Accounts)
        Assert.Equal<string list>([ @"C:\Thunderbird\Profiles\default" ], discovery.ProfilesFound)
        Assert.Single discovery.Unreadable |> ignore
        Assert.False discovery.SelectionCleared
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
            loadNoStoredAccounts
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
            loadNoStoredAccounts
            saveAccounts
            loadSelected
            saveSelected
            ()

    match actual with
    | Ok discovery ->
        // requirements.md -> "Selecting an account": clear it AND SAY SO. Clearing it in the store
        // is only half - without this flag the clearing is invisible past the workflow, and the
        // page can do nothing but let the tick vanish unexplained.
        Assert.True discovery.SelectionCleared
        Assert.Equal<DiscoveredMailAccount list>(accounts, discovery.Accounts)
        Assert.Equal<string list>([ @"C:\Thunderbird\Profiles\default" ], discovery.ProfilesFound)
        Assert.Single discovery.Unreadable |> ignore
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let cleared = Assert.Single selectionSaves
    Assert.Equal(None, cleared)

[<Fact; Trait("Level", "Unit")>]
let ``scanForMailAccounts reports no cleared selection when nothing was selected, and never writes one`` () =
    let discover, _ = recordingDiscover (discoveryResult [ account "1" ])
    let saveAccounts, _ = recordingSaveAccounts ()
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok None
    let saveSelected, selectionSaves = recordingSaveSelection ()

    let actual =
        ScanForMailAccountsWorkflow.scanForMailAccounts
            loadRootSet
            discover
            loadNoStoredAccounts
            saveAccounts
            loadSelected
            saveSelected
            ()

    match actual with
    | Ok discovery -> Assert.False discovery.SelectionCleared
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    // Nothing was selected, so there was nothing to clear - the store must not be written to.
    Assert.Empty selectionSaves

[<Fact; Trait("Level", "Unit")>]
let ``scanForMailAccounts carries a stored cached message count onto an account it found again`` () =
    // A scan re-reads prefs.js and re-enumerates folders; it does NOT count messages. Discovery
    // therefore reports every account with CachedMessageCount = None, and saving that straight over
    // the stored set threw away a figure whose header pass costs *minutes* on the measured profile
    // (design.md -> Decisions taken #4) - for an account the scan had just found again. The column
    // went back to "Not counted yet" with nothing said, and requirements.md's "state when it was
    // taken, because the count is a snapshot" only makes sense if a stale count survives to be
    // stated.
    let takenAt = System.DateTime(2026, 8, 20, 13, 45, 30, System.DateTimeKind.Utc)

    // The stored copy is deliberately STALE in every other field, so carrying the count forward is
    // provably not carrying the row forward.
    let stored =
        { account "1" with
            DisplayName = "Stale Name"
            EmailAddresses = [ "stale@example.com" ]
            StoreDirectoryExists = false
            CachedMessageCount = Some(42, takenAt) }

    let discovered = [ account "1"; account "2" ]
    let discover, _ = recordingDiscover (discoveryResult discovered)
    let saveAccounts, savedAccounts = recordingSaveAccounts ()
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok None
    let saveSelected, _ = recordingSaveSelection ()

    let actual =
        ScanForMailAccountsWorkflow.scanForMailAccounts
            loadRootSet
            discover
            (loadStoredAccounts [ stored ])
            saveAccounts
            loadSelected
            saveSelected
            ()

    let saved = Assert.Single(List.ofSeq savedAccounts)
    Assert.Equal(2, saved.Length)

    let savedFirst = saved |> List.find (fun a -> a.Id = accountId "1")
    let savedSecond = saved |> List.find (fun a -> a.Id = accountId "2")

    // The count is carried...
    Assert.Equal(Some(42, takenAt), savedFirst.CachedMessageCount)

    // ...and nothing else is: every other field is the fresh scan's answer, not the stored row's.
    Assert.Equal(@"C:\Thunderbird\Profiles\default", savedFirst.ProfilePath)
    Assert.Equal("Account 1", savedFirst.DisplayName)
    Assert.Equal<string list>([ "1@example.com" ], savedFirst.EmailAddresses)
    Assert.Equal(Mbox, savedFirst.StoreFormat)
    Assert.Equal(@"C:\Thunderbird\Profiles\default\ImapMail\example.com", savedFirst.StoreDirectory)
    Assert.True savedFirst.StoreDirectoryExists
    Assert.Empty savedFirst.Folders

    // An account with nothing stored against it keeps no count.
    Assert.Equal(None, savedSecond.CachedMessageCount)

    // What the page is handed has to agree with what was stored, or the table shows "Not counted
    // yet" until the next reload contradicts it.
    match actual with
    | Ok discovery -> Assert.Equal<DiscoveredMailAccount list>(saved, discovery.Accounts)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``scanForMailAccounts does not resurrect a stored account the fresh discovery no longer finds`` () =
    // Carrying a count forward must not become merging the two sets: a fresh scan is still the
    // whole truth about WHICH accounts exist.
    let takenAt = System.DateTime(2026, 8, 20, 13, 45, 30, System.DateTimeKind.Utc)
    let stored = { account "gone" with CachedMessageCount = Some(42, takenAt) }

    let discover, _ = recordingDiscover (discoveryResult [ account "1" ])
    let saveAccounts, savedAccounts = recordingSaveAccounts ()
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok None
    let saveSelected, _ = recordingSaveSelection ()

    ScanForMailAccountsWorkflow.scanForMailAccounts
        loadRootSet
        discover
        (loadStoredAccounts [ stored ])
        saveAccounts
        loadSelected
        saveSelected
        ()
    |> ignore

    let saved = Assert.Single(List.ofSeq savedAccounts)
    let only = Assert.Single saved
    Assert.Equal(accountId "1", only.Id)
    Assert.Equal(None, only.CachedMessageCount)

[<Fact; Trait("Level", "Unit")>]
let ``scanForMailAccounts fails with the store's error and never saves when the stored accounts cannot be read`` () =
    let discover, _ = recordingDiscover (discoveryResult [ account "1" ])
    let saveAccounts, savedAccounts = recordingSaveAccounts ()
    let loadPrevious: LoadMailAccounts = fun () -> Error(MailStoreFailed "Failed to load mail accounts.")
    let loadSelected: LoadSelectedMailAccount = fun () -> Ok None
    let saveSelected, selectionSaves = recordingSaveSelection ()

    let actual =
        ScanForMailAccountsWorkflow.scanForMailAccounts
            loadRootSet
            discover
            loadPrevious
            saveAccounts
            loadSelected
            saveSelected
            ()

    Assert.Equal(Error(MailStoreFailed "Failed to load mail accounts."), actual)
    Assert.Empty savedAccounts
    Assert.Empty selectionSaves

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
            loadNoStoredAccounts
            saveAccounts
            loadSelected
            saveSelected
            ()

    match actual with
    | Ok discovery -> Assert.Single discovery.Unreadable |> ignore
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
