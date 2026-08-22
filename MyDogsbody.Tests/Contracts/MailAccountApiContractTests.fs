module MyDogsbody.Tests.Contracts.MailAccountApiContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types
open MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

// MailAccountApi is a published interface, so one suite runs against the real API and against
// an in-memory fake of the kind a UI module-creator test or an E2E flow test would use for it.
// Without this, the UI suite could stay green over a fake that behaves in ways the real API
// never would.

let private handleError = HandleErrorBuilder(fun _ -> ())

// ---------- the real API, over a temp LiteDB file ----------

let private withRealApi (test: MailAccountApi -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test (MailAccountApiFactory.createMailAccountApi handleError context)
    finally
        context.Dispose()

        try
            File.Delete databasePath
        with _ ->
            ()

// ---------- the in-memory fake ----------
//
// A record literal rather than a class, because MailAccountApi is a record of functions.
// Discovery itself needs the filesystem, so the fake recognises the one committed fixture path
// (measuredShapeProfile) the same way the real API's ThunderbirdFolderScanner would, and treats
// every other path as "no profile found" - the same observable contract, without re-implementing
// prefs.js parsing here.

let private fakeAccount (id: string) : MailAccountUiType =
    {
        Id = id
        ProfilePath = measuredShapeProfile
        DisplayName = $"Account {id}"
        EmailAddresses = [ $"{id}@example.com" ]
        StoreFormat = "Mbox"
        StoreDirectory = $"C:\\{id}"
        StoreDirectoryExists = true
        Folders = [ { RelativePath = "INBOX"; DisplayName = "INBOX"; SizeBytes = 10L; IsScannable = true } ]
        CachedMessageCount = None
    }

let private withFakeApi (test: MailAccountApi -> unit) =
    let mutable profileRoot: string option = None
    let accounts = ResizeArray<MailAccountUiType>()
    let mutable selected: string option = None

    let fail action message = Error(MyDogsbodyException(action, message, ApplicationException message))

    test
        {
            GetProfileRoot = fun () -> Ok profileRoot

            SetProfileRoot =
                fun path ->
                    if not (Path.IsPathRooted path) then
                        fail ActionNames.MyDogsbody.Startup.MailAccountApi.setProfileRoot "Profile root path must be an absolute path."
                    else
                        profileRoot <- Some path
                        Ok()

            ScanForAccounts =
                fun () ->
                    match profileRoot with
                    | None -> fail ActionNames.MyDogsbody.Startup.MailAccountApi.scanForAccounts "No Thunderbird profile folder has been chosen yet."
                    | Some path when path = measuredShapeProfile ->
                        accounts.Clear()
                        accounts.AddRange [ for i in 1..10 -> fakeAccount $"fake-account-{i}" ]
                        Ok
                            {
                                Accounts = List.ofSeq accounts
                                ProfilesFound = [ path ]
                                Unreadable = []
                                // The fake clears no selection here: the accounts it produces are
                                // the same ten every time, so nothing selected can go missing.
                                SelectionCleared = false
                            }
                    | Some path -> fail ActionNames.MyDogsbody.Startup.MailAccountApi.scanForAccounts $"No Thunderbird profile was found under '{path}'."

            GetAccounts = fun () -> Ok(List.ofSeq accounts, selected)

            SelectAccount =
                fun id ->
                    if accounts |> Seq.exists (fun a -> a.Id = id) then
                        selected <- Some id
                        Ok()
                    else
                        fail ActionNames.MyDogsbody.Startup.MailAccountApi.selectAccount $"No mail account was found with id '{id}'."

            CountMessages =
                fun id ->
                    match accounts |> Seq.tryFindIndex (fun a -> a.Id = id) with
                    | None -> fail ActionNames.MyDogsbody.Startup.MailAccountApi.countMessages $"No mail account was found with id '{id}'."
                    | Some index ->
                        accounts.[index] <- { accounts.[index] with CachedMessageCount = Some(4, DateTime.UtcNow) }
                        Ok 4

            ClearWatermarks =
                fun id ->
                    if accounts |> Seq.exists (fun a -> a.Id = id) then
                        Ok()
                    else
                        fail ActionNames.MyDogsbody.Startup.MailAccountApi.clearWatermarks $"No mail account was found with id '{id}'."
        }

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq = [ [| box "real api" |]; [| box "fake api" |] ]

let private withImplementation (name: string) (test: MailAccountApi -> unit) =
    match name with
    | "real api" -> withRealApi test
    | "fake api" -> withFakeApi test
    | other -> failwith $"Unknown implementation '{other}'"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error(ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

let private errorOrFail label result =
    match result with
    | Error(ex: MyDogsbodyException) -> ex
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

// ---------- the shared suite ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``GetProfileRoot returns None before anything is chosen`` (implementation: string) =
    withImplementation implementation (fun api -> Assert.Equal(None, api.GetProfileRoot() |> okOrFail "GetProfileRoot"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SetProfileRoot then GetProfileRoot round trips the chosen folder`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        Assert.Equal(Some measuredShapeProfile, api.GetProfileRoot() |> okOrFail "GetProfileRoot"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``ScanForAccounts against the committed fixture finds ten accounts, and GetAccounts sees them`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts" |> ignore

        let discovery = api.ScanForAccounts() |> okOrFail "ScanForAccounts second"
        // Nothing was selected, so nothing can have been cleared - both implementations agree.
        Assert.False discovery.SelectionCleared

        let accounts, selected = api.GetAccounts() |> okOrFail "GetAccounts"
        Assert.Equal(10, accounts.Length)
        Assert.Equal(None, selected))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SelectAccount persists the selection, visible to a later GetAccounts`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        let discovery = api.ScanForAccounts() |> okOrFail "ScanForAccounts"
        let firstId = discovery.Accounts.Head.Id

        api.SelectAccount firstId |> okOrFail "SelectAccount"

        let _, selected = api.GetAccounts() |> okOrFail "GetAccounts"
        Assert.Equal(Some firstId, selected))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SelectAccount reports an unlogged error for an id not among the discovered accounts`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts" |> ignore

        let ex = api.SelectAccount "not-a-real-account-id" |> errorOrFail "SelectAccount"
        Assert.False(String.IsNullOrWhiteSpace ex.Message))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``CountMessages returns a count, and GetAccounts reflects a cached count afterwards`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        let discovery = api.ScanForAccounts() |> okOrFail "ScanForAccounts"
        let firstId = discovery.Accounts.Head.Id

        api.CountMessages firstId |> okOrFail "CountMessages" |> ignore

        let accounts, _ = api.GetAccounts() |> okOrFail "GetAccounts"
        let account = accounts |> List.find (fun a -> a.Id = firstId)
        Assert.True(account.CachedMessageCount.IsSome))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``every account GetAccounts returns names the profile it was discovered in`` (implementation: string) =
    // requirements.md -> "Walking the chosen folder" (Q4.9): accounts are listed "qualified by the
    // profile path they came from". `DiscoveredMailAccount` has carried `ProfilePath` since the
    // first commit and `DiscoveredAccountEntity` persists it, but the top mapper dropped it - so
    // the UI record, which is the whole of what the screen can see, had no way to tell one
    // profile's copy of an account from another's.
    withImplementation implementation (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts" |> ignore

        let accounts, _ = api.GetAccounts() |> okOrFail "GetAccounts"
        Assert.NotEmpty accounts
        Assert.All(accounts, (fun a -> Assert.Equal(measuredShapeProfile, a.ProfilePath))))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``ClearWatermarks succeeds for a known account`` (implementation: string) =
    withImplementation implementation (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        let discovery = api.ScanForAccounts() |> okOrFail "ScanForAccounts"

        api.ClearWatermarks discovery.Accounts.Head.Id |> okOrFail "ClearWatermarks")

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``ScanForAccounts reports an unlogged error when no profile folder has been chosen`` (implementation: string) =
    withImplementation implementation (fun api ->
        let ex = api.ScanForAccounts() |> errorOrFail "ScanForAccounts"
        Assert.False(String.IsNullOrWhiteSpace ex.Message))
