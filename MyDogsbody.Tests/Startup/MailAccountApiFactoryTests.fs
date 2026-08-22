module MyDogsbody.Tests.Startup.MailAccountApiFactoryTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types
open MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

let private handleError = HandleErrorBuilder(fun _ -> ())

/// Fresh temp LiteDB file per test, context disposed and the file deleted - no test reaches
/// Startup.Startup.
let private withApi (test: MailAccountApi -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "direct"
    let api = MailAccountApiFactory.createMailAccountApi handleError context

    try
        test api
    finally
        context.Dispose()

        try
            File.Delete databasePath
        with _ ->
            ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error(ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

let private errorOrFail label result =
    match result with
    | Error(ex: MyDogsbodyException) -> ex
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

let private alphaAccountId = $"{measuredShapeProfile}|account1"

[<Fact; Trait("Level", "Integration")>]
let ``GetProfileRoot returns None for a fresh database`` () =
    withApi (fun api -> Assert.Equal(None, api.GetProfileRoot() |> okOrFail "GetProfileRoot"))

[<Fact; Trait("Level", "Integration")>]
let ``SetProfileRoot then GetProfileRoot round trips the chosen folder`` () =
    withApi (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"

        let actual = api.GetProfileRoot() |> okOrFail "GetProfileRoot"
        Assert.Equal(Some measuredShapeProfile, actual))

[<Fact; Trait("Level", "Integration")>]
let ``SetProfileRoot rejects a relative path as an unlogged exception`` () =
    withApi (fun api ->
        let ex = api.SetProfileRoot "relative\\path" |> errorOrFail "SetProfileRoot"

        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.MailAccountApi.setProfileRoot, ex.ActionName))

[<Fact; Trait("Level", "Integration")>]
let ``ScanForAccounts finds exactly the ten accounts prefs.js declares against the measured-shape fixture`` () =
    withApi (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"

        let result = api.ScanForAccounts() |> okOrFail "ScanForAccounts"

        Assert.Equal(10, result.Accounts.Length)
        Assert.Equal<string list>([ measuredShapeProfile ], result.ProfilesFound)
        Assert.Empty result.Unreadable

        let alpha = result.Accounts |> List.find (fun a -> a.Id = alphaAccountId)
        Assert.Equal("Alpha Mail", alpha.DisplayName)
        Assert.NotEmpty alpha.Folders)

[<Fact; Trait("Level", "Integration")>]
let ``ScanForAccounts reports NoProfileFound as an unlogged exception when the folder has no prefs.js`` () =
    withApi (fun api ->
        let emptyFolder = Path.Combine(Path.GetTempPath(), $"mdb-empty-{Guid.NewGuid()}")
        Directory.CreateDirectory emptyFolder |> ignore

        try
            api.SetProfileRoot emptyFolder |> okOrFail "SetProfileRoot"

            let ex = api.ScanForAccounts() |> errorOrFail "ScanForAccounts"

            Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
            Assert.Equal(ActionNames.MyDogsbody.Startup.MailAccountApi.scanForAccounts, ex.ActionName)
        finally
            Directory.Delete(emptyFolder, true))

[<Fact; Trait("Level", "Integration")>]
let ``ScanForAccounts reports a chosen folder that no longer exists distinctly from one holding no profile`` () =
    withApi (fun api ->
        let goneFolder = Path.Combine(Path.GetTempPath(), $"mdb-gone-{Guid.NewGuid()}")
        Directory.CreateDirectory goneFolder |> ignore
        api.SetProfileRoot goneFolder |> okOrFail "SetProfileRoot"
        Directory.Delete(goneFolder, true)

        let gone = api.ScanForAccounts() |> errorOrFail "ScanForAccounts (folder deleted)"

        let emptyFolder = Path.Combine(Path.GetTempPath(), $"mdb-empty-{Guid.NewGuid()}")
        Directory.CreateDirectory emptyFolder |> ignore

        try
            api.SetProfileRoot emptyFolder |> okOrFail "SetProfileRoot 2"
            let empty = api.ScanForAccounts() |> errorOrFail "ScanForAccounts (folder empty)"

            // The two states need different answers from the user, so they must not share a
            // message (requirements.md -> "Choosing the profile folder" / "Edge cases").
            Assert.NotEqual<string>(empty.Message, gone.Message)
            Assert.Contains(goneFolder, gone.Message)
            Assert.IsType<ApplicationException>(gone.InnerException) |> ignore
            Assert.Equal(ActionNames.MyDogsbody.Startup.MailAccountApi.scanForAccounts, gone.ActionName)
        finally
            Directory.Delete(emptyFolder, true))

[<Fact; Trait("Level", "Integration")>]
let ``a scan against a folder that has gone away keeps the stored path so the user can see what it was`` () =
    withApi (fun api ->
        let goneFolder = Path.Combine(Path.GetTempPath(), $"mdb-gone-{Guid.NewGuid()}")
        Directory.CreateDirectory goneFolder |> ignore
        api.SetProfileRoot goneFolder |> okOrFail "SetProfileRoot"
        Directory.Delete(goneFolder, true)

        api.ScanForAccounts() |> errorOrFail "ScanForAccounts" |> ignore

        Assert.Equal(Some goneFolder, api.GetProfileRoot() |> okOrFail "GetProfileRoot"))

[<Fact; Trait("Level", "Integration")>]
let ``GetAccounts returns what a previous scan stored, with no selection`` () =
    withApi (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts" |> ignore

        let accounts, selected = api.GetAccounts() |> okOrFail "GetAccounts"

        Assert.Equal(10, accounts.Length)
        Assert.Equal(None, selected))

[<Fact; Trait("Level", "Integration")>]
let ``SelectAccount persists the selection and GetAccounts reflects it`` () =
    withApi (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts" |> ignore

        api.SelectAccount alphaAccountId |> okOrFail "SelectAccount"

        let _, selected = api.GetAccounts() |> okOrFail "GetAccounts"
        Assert.Equal(Some alphaAccountId, selected))

[<Fact; Trait("Level", "Integration")>]
let ``SelectAccount rejects an id not among the discovered accounts as an unlogged exception`` () =
    withApi (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts" |> ignore

        let ex = api.SelectAccount "unknown-account" |> errorOrFail "SelectAccount"

        Assert.IsType<ApplicationException>(ex.InnerException) |> ignore
        Assert.Equal(ActionNames.MyDogsbody.Startup.MailAccountApi.selectAccount, ex.ActionName))

[<Fact; Trait("Level", "Integration")>]
let ``CountMessages runs a headers-only pass and caches the result, visible on the next GetAccounts`` () =
    withApi (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts" |> ignore

        let count = api.CountMessages alphaAccountId |> okOrFail "CountMessages"
        Assert.Equal(4, count)

        let accounts, _ = api.GetAccounts() |> okOrFail "GetAccounts"
        let alpha = accounts |> List.find (fun a -> a.Id = alphaAccountId)

        match alpha.CachedMessageCount with
        | Some(cachedCount, _takenAt) -> Assert.Equal(4, cachedCount)
        | None -> Assert.Fail("Expected a cached message count after CountMessages"))

[<Fact; Trait("Level", "Integration")>]
let ``ClearWatermarks succeeds for a known account`` () =
    withApi (fun api ->
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts" |> ignore

        api.ClearWatermarks alphaAccountId |> okOrFail "ClearWatermarks")

[<Fact; Trait("Level", "Integration")>]
let ``a fresh scan clears a selection naming an account absent from it`` () =
    withApi (fun api ->
        // Two profile roots: one with the alpha family of accounts, one wholly different -
        // rescanning against the second must clear a selection pointing at the first.
        api.SetProfileRoot measuredShapeProfile |> okOrFail "SetProfileRoot 1"
        api.ScanForAccounts() |> okOrFail "ScanForAccounts 1" |> ignore
        api.SelectAccount alphaAccountId |> okOrFail "SelectAccount"

        let otherProfile = Path.Combine(Path.GetTempPath(), $"mdb-other-{Guid.NewGuid()}")
        Directory.CreateDirectory otherProfile |> ignore

        try
            File.WriteAllText(
                Path.Combine(otherProfile, "prefs.js"),
                "user_pref(\"mail.account.lastKey\", 0);\nuser_pref(\"mail.accountmanager.accounts\", \"\");"
            )

            api.SetProfileRoot otherProfile |> okOrFail "SetProfileRoot 2"
            api.ScanForAccounts() |> okOrFail "ScanForAccounts 2" |> ignore

            let _, selected = api.GetAccounts() |> okOrFail "GetAccounts"
            Assert.Equal(None, selected)
        finally
            Directory.Delete(otherProfile, true))
