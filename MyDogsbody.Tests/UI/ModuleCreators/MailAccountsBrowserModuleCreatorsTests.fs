module MyDogsbody.Tests.UI.ModuleCreators.MailAccountsBrowserModuleCreatorsTests

open System
open Xunit
open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types

/// Runs the work on the calling thread. Production passes an Async.Start equivalent; the seam
/// exists so a test never has to wait on a background thread.
let private runSynchronously (work: unit -> unit) = work ()

let private failure message = MyDogsbodyException("test.action", message, ApplicationException(message))

let private anAccount id : MailAccountUiType =
    {
        Id = id
        ProfilePath = $"C:\\profiles\\{id}"
        DisplayName = $"Account {id}"
        EmailAddresses = [ $"{id}@example.com" ]
        StoreFormat = "Mbox"
        StoreDirectory = $"C:\\{id}"
        StoreDirectoryExists = true
        Folders = []
        CachedMessageCount = None
    }

let private aDiscoveryResult accounts : DiscoveryResultUiType =
    { Accounts = accounts; ProfilesFound = []; Unreadable = []; SelectionCleared = false }

/// A fake `MailAccountApi` with defaults that succeed and do nothing, overridable per test.
let private api
    (getProfileRoot: unit -> Result<string option, MyDogsbodyException>)
    (setProfileRoot: string -> Result<unit, MyDogsbodyException>)
    (scanForAccounts: unit -> Result<DiscoveryResultUiType, MyDogsbodyException>)
    (getAccounts: unit -> Result<MailAccountUiType list * string option, MyDogsbodyException>)
    (selectAccount: string -> Result<unit, MyDogsbodyException>)
    (countMessages: string -> Result<int, MyDogsbodyException>)
    (clearWatermarks: string -> Result<unit, MyDogsbodyException>)
    : MailAccountApi =
    {
        GetProfileRoot = getProfileRoot
        SetProfileRoot = setProfileRoot
        ScanForAccounts = scanForAccounts
        GetAccounts = getAccounts
        SelectAccount = selectAccount
        CountMessages = countMessages
        ClearWatermarks = clearWatermarks
    }

let private defaultApi () =
    api (fun () -> Ok None) (fun _ -> Ok()) (fun () -> Ok(aDiscoveryResult [])) (fun () -> Ok([], None)) (fun _ -> Ok()) (fun _ -> Ok 0) (fun _ -> Ok())

[<Fact; Trait("Level", "Unit")>]
let ``the module loads the profile root and the accounts when it is created`` () =
    let mailAccountApi =
        api
            (fun () -> Ok(Some @"C:\Thunderbird"))
            (fun _ -> Ok())
            (fun () -> Ok(aDiscoveryResult []))
            (fun () -> Ok([ anAccount "1" ], Some "1"))
            (fun _ -> Ok())
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    Assert.Equal(Some @"C:\Thunderbird", AVal.force browser.ProfileRootAval)
    let listed = AVal.force browser.AccountsAval
    Assert.Single listed |> ignore
    Assert.Equal(Some "1", AVal.force browser.SelectedAccountIdAval)
    Assert.False(AVal.force browser.IsLoadingAval)
    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed initial load surfaces the message and stops loading`` () =
    let mailAccountApi =
        api (fun () -> Ok None) (fun _ -> Ok()) (fun () -> Ok(aDiscoveryResult [])) (fun () -> Error(failure "could not read accounts")) (fun _ -> Ok()) (fun _ -> Ok 0) (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    Assert.Equal(Some "could not read accounts", AVal.force browser.ErrorAval)
    Assert.Empty(AVal.force browser.AccountsAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``SetProfileRoot passes the path through and updates the displayed root`` () =
    let received = ResizeArray<string>()
    let stored = ref None

    let mailAccountApi =
        api
            (fun () -> Ok stored.Value)
            (fun path ->
                received.Add path
                stored.Value <- Some path
                Ok())
            (fun () -> Ok(aDiscoveryResult []))
            (fun () -> Ok([], None))
            (fun _ -> Ok())
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    browser.SetProfileRoot @"C:\chosen"

    Assert.Single received |> ignore
    Assert.Equal(@"C:\chosen", received.[0])
    Assert.Equal(Some @"C:\chosen", AVal.force browser.ProfileRootAval)

[<Fact; Trait("Level", "Unit")>]
let ``SetProfileRoot displays the root the store kept, not the string it was handed`` () =
    // ProfileRootPath.create trims, so what SetProfileRoot persists is not always the string it
    // was given. Echoing the input back into the page left it showing a value the store does not
    // hold - CLAUDE-project.md -> UI: "A write reloads. Every command that changes stored data
    // calls the load function on success, so the table shows what was stored rather than what
    // the dialog held."
    let stored = ref None

    let mailAccountApi =
        api
            (fun () -> Ok stored.Value)
            (fun path ->
                stored.Value <- Some(path.Trim())
                Ok())
            (fun () -> Ok(aDiscoveryResult []))
            (fun () -> Ok([], None))
            (fun _ -> Ok())
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    browser.SetProfileRoot "  C:\\chosen  "

    Assert.Equal(Some @"C:\chosen", AVal.force browser.ProfileRootAval)
    Assert.Equal(None, AVal.force browser.ErrorAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``choosing a folder then scanning reloads the accounts table`` () =
    let storedAccounts = ResizeArray<MailAccountUiType>()
    let scanCalls = ref 0

    let mailAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Ok())
            (fun () ->
                scanCalls.Value <- scanCalls.Value + 1
                storedAccounts.Add(anAccount "1")
                Ok(aDiscoveryResult(List.ofSeq storedAccounts)))
            (fun () -> Ok(List.ofSeq storedAccounts, None))
            (fun _ -> Ok())
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    Assert.Empty(AVal.force browser.AccountsAval)

    browser.SetProfileRoot @"C:\chosen"
    browser.ScanForAccounts()

    Assert.Equal(1, scanCalls.Value)
    let listed = AVal.force browser.AccountsAval
    Assert.Single listed |> ignore
    Assert.False(AVal.force browser.IsScanningAval)

[<Fact; Trait("Level", "Unit")>]
let ``ScanForAccounts surfaces the unreadable directories from the scan result`` () =
    let mailAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Ok())
            (fun () -> Ok { aDiscoveryResult [] with Unreadable = [ { Path = @"C:\denied"; Reason = "Access denied" } ] })
            (fun () -> Ok([], None))
            (fun _ -> Ok())
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    browser.ScanForAccounts()

    let unreadable = AVal.force browser.UnreadableAval
    let entry = Assert.Single unreadable
    Assert.Equal(@"C:\denied", entry.Path)

[<Fact; Trait("Level", "Unit")>]
let ``ScanForAccounts surfaces a cleared selection, and a later scan that clears nothing takes it back down`` () =
    let clearedOnNextScan = ref true

    let mailAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Ok())
            (fun () -> Ok { aDiscoveryResult [] with SelectionCleared = clearedOnNextScan.Value })
            (fun () -> Ok([], None))
            (fun _ -> Ok())
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    // Nothing has been scanned yet, so there is nothing to announce.
    Assert.False(AVal.force browser.SelectionClearedAval)

    browser.ScanForAccounts()
    Assert.True(AVal.force browser.SelectionClearedAval)
    // Announcing a reconciliation is not reporting a failure - the alert channel stays free.
    Assert.Equal(None, AVal.force browser.ErrorAval)

    clearedOnNextScan.Value <- false
    browser.ScanForAccounts()
    Assert.False(AVal.force browser.SelectionClearedAval)

[<Fact; Trait("Level", "Unit")>]
let ``SelectAccount takes the cleared-selection notice down, and a failed one leaves it up`` () =
    // The notice the scan raises reads "...the selection has been cleared. Choose an account
    // below." Once the user has chosen one its premise is false, so it must come down here rather
    // than waiting for some later scan that happens to clear nothing.
    let selectSucceeds = ref false

    let mailAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Ok())
            (fun () -> Ok { aDiscoveryResult [ anAccount "1" ] with SelectionCleared = true })
            (fun () -> Ok([ anAccount "1" ], None))
            (fun _ -> if selectSucceeds.Value then Ok() else Error(failure "could not select"))
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    browser.ScanForAccounts()
    Assert.True(AVal.force browser.SelectionClearedAval)

    // A select that failed answered nothing, so the notice stays up and the failure is reported.
    browser.SelectAccount "1"
    Assert.True(AVal.force browser.SelectionClearedAval)
    Assert.Equal(Some "could not select", AVal.force browser.ErrorAval)

    selectSucceeds.Value <- true
    browser.SelectAccount "1"
    Assert.False(AVal.force browser.SelectionClearedAval)
    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed scan surfaces the message and stops the scanning indicator`` () =
    let mailAccountApi =
        api (fun () -> Ok None) (fun _ -> Ok()) (fun () -> Error(failure "no profile found")) (fun () -> Ok([], None)) (fun _ -> Ok()) (fun _ -> Ok 0) (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    browser.ScanForAccounts()

    Assert.Equal(Some "no profile found", AVal.force browser.ErrorAval)
    Assert.False(AVal.force browser.IsScanningAval)

[<Fact; Trait("Level", "Unit")>]
let ``SelectAccount persists the selection and reloads`` () =
    let selectedIds = ResizeArray<string>()
    let loads = ref 0

    let mailAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Ok())
            (fun () -> Ok(aDiscoveryResult []))
            (fun () ->
                loads.Value <- loads.Value + 1
                Ok([ anAccount "1" ], if selectedIds.Count > 0 then Some selectedIds.[0] else None))
            (fun id -> selectedIds.Add id; Ok())
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi
    let loadsAfterInit = loads.Value

    browser.SelectAccount "1"

    Assert.Single selectedIds |> ignore
    Assert.Equal("1", selectedIds.[0])
    Assert.Equal(Some "1", AVal.force browser.SelectedAccountIdAval)
    Assert.True(loads.Value > loadsAfterInit, "expected a reload after selecting an account")

[<Fact; Trait("Level", "Unit")>]
let ``a failed select surfaces the message`` () =
    let mailAccountApi =
        api (fun () -> Ok None) (fun _ -> Ok()) (fun () -> Ok(aDiscoveryResult [])) (fun () -> Ok([], None)) (fun _ -> Error(failure "unknown account")) (fun _ -> Ok 0) (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    browser.SelectAccount "unknown"

    Assert.Equal(Some "unknown account", AVal.force browser.ErrorAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``CountMessages passes the id through and reloads so the cached count appears`` () =
    let counted = ResizeArray<string>()
    let loads = ref 0

    let mailAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Ok())
            (fun () -> Ok(aDiscoveryResult []))
            (fun () -> loads.Value <- loads.Value + 1; Ok([], None))
            (fun _ -> Ok())
            (fun id -> counted.Add id; Ok 4)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi
    let loadsAfterInit = loads.Value

    browser.CountMessages "1"

    Assert.Single counted |> ignore
    Assert.Equal("1", counted.[0])
    Assert.True(loads.Value > loadsAfterInit, "expected a reload after counting messages")

[<Fact; Trait("Level", "Unit")>]
let ``ClearWatermarks passes the id through`` () =
    let cleared = ResizeArray<string>()

    let mailAccountApi =
        api (fun () -> Ok None) (fun _ -> Ok()) (fun () -> Ok(aDiscoveryResult [])) (fun () -> Ok([], None)) (fun _ -> Ok()) (fun _ -> Ok 0) (fun id -> cleared.Add id; Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    browser.ClearWatermarks "1"

    Assert.Single cleared |> ignore
    Assert.Equal("1", cleared.[0])

[<Fact; Trait("Level", "Unit")>]
let ``a later success clears an earlier error`` () =
    let attempts = ref 0

    let mailAccountApi =
        api
            (fun () -> Ok None)
            (fun _ -> Ok())
            (fun () -> Ok(aDiscoveryResult []))
            (fun () -> Ok([], None))
            (fun _ ->
                attempts.Value <- attempts.Value + 1
                if attempts.Value = 1 then Error(failure "first attempt failed") else Ok())
            (fun _ -> Ok 0)
            (fun _ -> Ok())

    let browser = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule runSynchronously mailAccountApi

    browser.SelectAccount "1"
    Assert.Equal(Some "first attempt failed", AVal.force browser.ErrorAval)

    browser.SelectAccount "1"

    Assert.Equal(None, AVal.force browser.ErrorAval)

/// Walks up from the test assembly rather than hard-coding a path, so this keeps working
/// whatever the working directory or build configuration is.
let private repositoryRoot () =
    let rec find (directory: IO.DirectoryInfo) =
        if isNull (box directory) then
            failwith "Could not locate MyDogsbody.sln above the test assembly."
        elif IO.File.Exists(IO.Path.Combine(directory.FullName, "MyDogsbody.sln")) then
            directory.FullName
        else
            find directory.Parent

    find (IO.DirectoryInfo(AppContext.BaseDirectory))

[<Fact; Trait("Level", "Unit")>]
let ``no Async.Start appears anywhere in the module creator file`` () =
    let sourceFilePath =
        IO.Path.Combine(repositoryRoot (), "MyDogsbody.UI.Portal", "ModuleCreators", "MailAccountsBrowserModuleCreators.fs")

    Assert.True(IO.File.Exists sourceFilePath, $"Expected to find {sourceFilePath}")
    let source = IO.File.ReadAllText sourceFilePath
    Assert.DoesNotContain("Async.Start(", source)
